using System.Collections.Concurrent;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>
/// 页面查询协调器：扫描候选、形成能力来源计划、执行计划并维护成功/失败状态。
/// 页面引擎只依赖这个小接口，不理解供应商端点或能力合并规则。
/// </summary>
public sealed class PageRuntimeCoordinator : IPageRuntimeCoordinator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    private readonly QueryMethodRegistry _registry;
    private readonly FingerprintBuilder _fingerprints;
    private readonly MethodStateStore _stateStore;
    private readonly MethodResultCache _cache;
    private readonly ZCodeUsageService _zcode;
    private readonly PageRuntimeStateStore _runtimeState;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pageLocks = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();

    public PageRuntimeCoordinator(
        QueryMethodRegistry registry,
        FingerprintBuilder fingerprints,
        MethodStateStore stateStore,
        MethodResultCache cache,
        ZCodeUsageService zcode,
        PageRuntimeStateStore? runtimeState = null)
    {
        _registry = registry;
        _fingerprints = fingerprints;
        _stateStore = stateStore;
        _cache = cache;
        _zcode = zcode;
        _runtimeState = runtimeState ?? new PageRuntimeStateStore();
    }

    public Task<PageRuntimeResult> RefreshAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
        => WithPageLock(page.Id, () => RefreshCoreAsync(page, reason, ct), ct);

    public async Task<ScanReport> RescanAsync(PageConfigRecord page, ScanReason reason, CancellationToken ct)
    {
        var result = await WithPageLock(page.Id, () => RescanAndQueryAsync(page, ToRefreshReason(reason), ct), ct);
        _consecutiveFailures[page.Id] = 0;
        return result.Scan!;
    }

    public bool TryGetSnapshot(string pageId, out CapabilitySnapshot snapshot)
        => _runtimeState.TryGetSnapshot(pageId, out snapshot);

    public bool TryGetScanReport(string pageId, out ScanReport report)
        => _runtimeState.TryGetScan(pageId, out report);

    /// <summary>临时覆盖只存于进程内；不写入 PageMethodState 或页面配置。</summary>
    public void SetTemporaryOverride(string pageId, string? methodId)
    {
        _runtimeState.SetTemporaryOverride(pageId, methodId);
        _cache.InvalidatePage(pageId);
    }

    private async Task<PageRuntimeResult> RefreshCoreAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
    {
        var fingerprint = _fingerprints.Build(page, _zcode.DatabaseExists);
        var persisted = _stateStore.Load(page.Id);
        var runtime = _runtimeState.GetOrCreate(page.Id);
        var configurationChanged = runtime.Fingerprint is not null && runtime.Fingerprint != fingerprint;

        var needRescan = reason is RefreshReason.PageSaved
            or RefreshReason.ConfigurationChanged
            or RefreshReason.FingerprintChanged
            or RefreshReason.ConsecutiveFailures
            or RefreshReason.Manual
            || persisted is null
            || persisted.Fingerprint != fingerprint
            || persisted.Candidates.Count == 0;

        if (configurationChanged || reason is RefreshReason.ConfigurationChanged or RefreshReason.FingerprintChanged)
        {
            _runtimeState.ClearTemporaryOverride(page.Id);
            _cache.InvalidatePage(page.Id);
            needRescan = true;
        }

        if (needRescan)
            return await RescanAndQueryAsync(page, reason, ct);

        var plan = CapabilitySourcePlan.Build(
            persisted!.Candidates,
            persisted.SelectedMethodIdsByCapability ?? new Dictionary<CapabilityKind, string>(),
            _runtimeState.TemporaryOverrideFor(page.Id));
        runtime.Fingerprint = fingerprint;
        runtime.Plan = plan;
        return await PollQueryAsync(page, persisted, plan, fingerprint, ct, reason);
    }

    private async Task<PageRuntimeResult> RescanAndQueryAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
    {
        // 重扫代表重新确认来源，之前的人工覆盖必须失效。
        _runtimeState.ClearTemporaryOverride(page.Id);
        var fingerprint = _fingerprints.Build(page, _zcode.DatabaseExists);
        var context = new ScanContext
        {
            Page = page,
            ConfigurationFingerprint = fingerprint,
            Credentials = new CredentialResolver(page),
            CancellationToken = ct,
        };

        var candidates = new List<MethodCandidate>(_registry.Methods.Count);
        foreach (var method in _registry.Methods)
        {
            var descriptor = method.Describe();
            // 私有兼容方法未显式启用时不执行扫描，也不出现在候选链中。
            if (descriptor.Enablement == MethodEnablement.PrivateCompatOnly
                && !page.EnabledCompatibilityMethods.Contains(descriptor.MethodId, StringComparer.Ordinal))
                continue;

            try
            {
                candidates.Add(await method.ScanAsync(page, context, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogException($"scan {descriptor.MethodId}", ex);
                candidates.Add(MethodSupport.NotAvailable(descriptor, QueryFailureClassifier.StatusOf(ex), "扫描异常"));
            }
        }

        var ordered = CandidateSelector.Order(candidates);
        var plan = CapabilitySourcePlan.Build(ordered);
        var report = new ScanReport(page.Id, fingerprint, ordered, plan, DateTimeOffset.UtcNow);
        var persisted = new PageMethodState
        {
            PageId = page.Id,
            Fingerprint = fingerprint,
            ScannedAt = report.ScannedAt,
            Candidates = ordered.ToList(),
            SelectedMethodIdsByCapability = plan.SelectedMethodIds.ToDictionary(pair => pair.Key, pair => pair.Value),
            SelectedMethodId = plan.PrimaryMethodId,
            SelectionStatus = plan.OverallStatus,
        };

        // 重扫重新确认来源，不能复用旧方法结果或临时覆盖的查询计划。
        _cache.InvalidatePage(page.Id);
        _stateStore.Save(persisted);

        var runtime = _runtimeState.GetOrCreate(page.Id);
        runtime.Fingerprint = fingerprint;
        runtime.Scan = report;
        runtime.Plan = plan;
        var aggregate = await QuerySelectedAsync(page, plan, fingerprint, reason, ct);
        runtime.Snapshot = aggregate.Snapshot;
        runtime.LastFailure = aggregate.Failure;
        runtime.LastAttemptAt = DateTimeOffset.UtcNow;
        _consecutiveFailures[page.Id] = 0;

        var authClass = ResolveAuthCredentialClass(page, ordered, plan, aggregate.Failure);
        return new PageRuntimeResult(page.Id, aggregate.Snapshot, report, aggregate.Failure, authClass);
    }

    private async Task<PageRuntimeResult> PollQueryAsync(
        PageConfigRecord page,
        PageMethodState state,
        CapabilitySourcePlan plan,
        string fingerprint,
        CancellationToken ct,
        RefreshReason reason)
    {
        var runtime = _runtimeState.GetOrCreate(page.Id);
        var aggregate = await QuerySelectedAsync(page, plan, fingerprint, reason, ct);
        runtime.Snapshot = aggregate.Snapshot;
        runtime.LastFailure = aggregate.Failure;
        runtime.LastAttemptAt = DateTimeOffset.UtcNow;

        if (aggregate.Snapshot.Capabilities.All(c => c.Kind == CapabilityKind.ProbeDiagnostic)
            && aggregate.Failure is not null)
        {
            var count = _consecutiveFailures.TryGetValue(page.Id, out var n) ? n + 1 : 1;
            _consecutiveFailures[page.Id] = count;
            if (count >= RetryPolicy.RescanAfterConsecutiveFailures)
            {
                Logger.Log($"page {page.Id}: 连续失败 {count} 次，触发重新扫描");
                _consecutiveFailures[page.Id] = 0;
                return await RescanAndQueryAsync(page, RefreshReason.ConsecutiveFailures, ct);
            }
        }
        else
        {
            _consecutiveFailures.TryRemove(page.Id, out _);
        }

        var authClass = ResolveAuthCredentialClass(page, state.Candidates, plan, aggregate.Failure);
        return new PageRuntimeResult(page.Id, aggregate.Snapshot, null, aggregate.Failure, authClass);
    }

    private static CredentialClass? ResolveAuthCredentialClass(
        PageConfigRecord page,
        IEnumerable<MethodCandidate> candidates,
        CapabilitySourcePlan plan,
        FailureInfo? failure)
    {
        var scannedSessionCandidate = candidates.FirstOrDefault(candidate =>
            candidate.Status == CandidateStatus.AuthRequired
            && candidate.Method.CredentialClass is CredentialClass.OAuthSession or CredentialClass.ConsoleSession);
        if (scannedSessionCandidate is not null)
            return scannedSessionCandidate.Method.CredentialClass;

        // 轮询期间凭据可能从“可用”变为 401；此时扫描状态仍是 Available，
        // 根据页面凭据或本轮计划中的会话来源恢复正确的登录入口。
        if (failure?.Status != CandidateStatus.AuthRequired)
            return null;
        var declared = page.CredentialRef.ResolveClass();
        if (declared is CredentialClass.OAuthSession or CredentialClass.ConsoleSession)
            return declared;
        return plan.SelectedCandidates.FirstOrDefault(candidate =>
            candidate.Method.CredentialClass is CredentialClass.OAuthSession or CredentialClass.ConsoleSession)
            ?.Method.CredentialClass;
    }

    private async Task<QueryAggregate> QuerySelectedAsync(
        PageConfigRecord page,
        CapabilitySourcePlan plan,
        string fingerprint,
        RefreshReason reason,
        CancellationToken ct)
    {
        var values = new List<CapabilityValue>();
        var itemKeys = new HashSet<CapabilityItemKey>();
        var failures = new List<FailureInfo>();
        var effectiveSelections = plan.SelectedByCapability.ToDictionary(pair => pair.Key, pair => pair.Value);
        var results = new Dictionary<string, MethodQueryResult>(StringComparer.Ordinal);

        foreach (var candidate in plan.SelectedCandidates)
        {
            if (_registry.Find(candidate.Method.MethodId) is not { } method)
                continue;

            var methodId = candidate.Method.MethodId;
            var result = await QueryMethodAsync(page, method, candidate, fingerprint, ct);
            results[methodId] = result;
            AddValuesForMethod(methodId, result, effectiveSelections, values, itemKeys);
            if (result.Failure is not null)
                failures.Add(result.Failure);
        }

        // 当前来源失败时只为受影响的能力槽尝试候选链中的回退来源，不能把两个来源的值相加。
        foreach (var selection in plan.Selections.Values)
        {
            if (selection.Selected is not { } selected
                || !results.TryGetValue(selected.Method.MethodId, out var primaryResult)
                || primaryResult.Failure is null)
                continue;

            var fallbackCandidates = CandidateSelector.Order(selection.Candidates
                .Where(candidate => candidate.IsAvailable
                                    && candidate.Method.MethodId != selected.Method.MethodId));
            var fallbackSelection = CandidateSelector.Select(fallbackCandidates);
            if (fallbackSelection.Status == CandidateStatus.RequiresSelection)
            {
                failures.Add(new FailureInfo(CandidateStatus.RequiresSelection,
                    $"能力 {selection.Capability} 的回退来源存在并列", DateTimeOffset.UtcNow));
                continue;
            }
            if (fallbackSelection.SelectedMethodId is not { } fallbackId)
                continue;

            if (!results.TryGetValue(fallbackId, out var fallbackResult))
            {
                if (_registry.Find(fallbackId) is not { } fallbackMethod)
                    continue;
                var fallbackCandidate = fallbackCandidates.First(candidate => candidate.Method.MethodId == fallbackId);
                fallbackResult = await QueryMethodAsync(page, fallbackMethod, fallbackCandidate, fingerprint, ct);
                results[fallbackId] = fallbackResult;
                if (fallbackResult.Failure is not null)
                    failures.Add(fallbackResult.Failure);
            }

            if (fallbackResult.Failure is null
                && fallbackResult.Capabilities.Any(capability => capability.Kind == selection.Capability
                                                                   && !capability.IsStale))
            {
                values.RemoveAll(value => value.Kind == selection.Capability);
                itemKeys.Clear();
                foreach (var value in values) itemKeys.Add(CapabilityItemKey.For(value));
                var fallback = fallbackCandidates.First(candidate => candidate.Method.MethodId == fallbackId);
                effectiveSelections[selection.Capability] = fallback;
                AddValuesForMethod(fallbackId, fallbackResult, effectiveSelections, values, itemKeys,
                    new[] { selection.Capability });
            }
        }

        if (plan.RequiresSelection)
            failures.Add(new FailureInfo(CandidateStatus.RequiresSelection, "能力来源并列，需要人工选择", DateTimeOffset.UtcNow));
        foreach (var selection in plan.Selections.Values.Where(selection => selection.Selected is null))
        {
            if (selection.Status == CandidateStatus.RequiresSelection) continue;
            if (selection.Status is CandidateStatus.Unsupported or CandidateStatus.NoReliableUsage) continue;
            failures.Add(new FailureInfo(selection.Status,
                $"能力 {selection.Capability} 没有可用来源", DateTimeOffset.UtcNow));
        }

        var status = ResolveSnapshotStatus(values, failures, plan);
        var fetchedAt = values.Count > 0
            ? values.Max(value => value.FetchedAt)
            : failures.Count > 0 ? failures.Max(failure => failure.At) : DateTimeOffset.UtcNow;
        var selectedIds = effectiveSelections.Values
            .Select(candidate => candidate.Method.MethodId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata(
                page.Id,
                fingerprint,
                fetchedAt,
                selectedIds.FirstOrDefault(),
                reason,
                selectedIds),
            Status = status,
            Capabilities = values,
        };
        return new QueryAggregate(snapshot, failures.FirstOrDefault());
    }

    private static void AddValuesForMethod(
        string methodId,
        MethodQueryResult result,
        IReadOnlyDictionary<CapabilityKind, MethodCandidate> selections,
        List<CapabilityValue> values,
        HashSet<CapabilityItemKey> itemKeys,
        IEnumerable<CapabilityKind>? only = null)
    {
        var allowed = only?.ToHashSet() ?? selections
            .Where(pair => pair.Value.Method.MethodId == methodId)
            .Select(pair => pair.Key)
            .ToHashSet();
        foreach (var capability in result.Capabilities)
        {
            // 只有当前能力槽实际选中的方法才能贡献该能力，避免多能力方法越权填充其他来源的槽。
            if (!allowed.Contains(capability.Kind)) continue;
            if (itemKeys.Add(CapabilityItemKey.For(capability)))
                values.Add(capability);
        }
    }

    private async Task<MethodQueryResult> QueryMethodAsync(
        PageConfigRecord page,
        IQueryMethod method,
        MethodCandidate candidate,
        string fingerprint,
        CancellationToken ct)
    {
        var descriptor = method.Describe();
        var key = MethodResultCache.MethodKey(page.Id, fingerprint, descriptor.MethodId, descriptor.ImplementationVersion);
        if (_cache.TryGet(key, out var cached)
            && cached.HasLastSuccessfulSnapshot
            && !cached.IsStale(CacheTtl))
        {
            return new MethodQueryResult(
                cached.LastSuccessfulSnapshot.Capabilities,
                cached.LastSuccessfulSnapshot.Status,
                null,
                cached.LastSuccessfulSnapshot.Metadata.FetchedAt);
        }

        MethodQueryResult? result = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                result = await method.QueryAsync(page, candidate, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = FailedResult(CandidateStatus.NetworkFailure, "请求超时");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                result = FailedResult(QueryFailureClassifier.StatusOf(ex), QueryFailureClassifier.ReasonOf(ex));
            }
            catch (Exception ex)
            {
                Logger.LogException($"query {descriptor.MethodId}", ex);
                result = FailedResult(QueryFailureClassifier.StatusOf(ex), QueryFailureClassifier.ReasonOf(ex));
            }

            if (result is null || result.Failure is null || !RetryPolicy.ShouldRetry(result.Failure.Status, attempt))
                break;
            await Task.Delay(RetryPolicy.Backoff(attempt), ct);
        }

        result ??= FailedResult(CandidateStatus.NetworkFailure, "查询未返回结果");
        _cache.RecordAttempt(key, result);

        if (IsSuccessfulResult(result))
        {
            var snapshot = new CapabilitySnapshot
            {
                Metadata = new SnapshotMetadata(page.Id, fingerprint, result.FetchedAt, descriptor.MethodId, RefreshReason.Poll,
                    new[] { descriptor.MethodId }),
                Status = result.Status,
                Capabilities = result.Capabilities,
            };
            _cache.Put(key, snapshot, result);
            return result;
        }

        if (_cache.TryGet(key, out var stale)
            && stale.HasLastSuccessfulSnapshot)
        {
            var staleCapabilities = stale.LastSuccessfulSnapshot.Capabilities
                .Select(capability => capability with { IsStale = true })
                .ToList();
            return new MethodQueryResult(
                staleCapabilities,
                SnapshotStatus.Stale,
                result.Failure,
                stale.LastSuccessfulSnapshot.Metadata.FetchedAt);
        }
        return result;
    }

    private static bool IsSuccessfulResult(MethodQueryResult result)
        => result.Capabilities.Count > 0
           && result.Status is SnapshotStatus.Success or SnapshotStatus.SuccessPartial or SnapshotStatus.ProbeOnly;

    private static MethodQueryResult FailedResult(CandidateStatus status, string reason)
    {
        var snapshotStatus = QueryFailureClassifier.SnapshotStatusOf(status);
        return new MethodQueryResult(
            Array.Empty<CapabilityValue>(),
            snapshotStatus,
            new FailureInfo(status, reason, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
    }

    private static SnapshotStatus ResolveSnapshotStatus(
        IReadOnlyList<CapabilityValue> values,
        IReadOnlyList<FailureInfo> failures,
        CapabilitySourcePlan plan)
    {
        var usageValues = values.Where(value => value.Kind != CapabilityKind.ProbeDiagnostic).ToList();
        var freshUsage = usageValues.Any(value => !value.IsStale);
        var staleUsage = usageValues.Any(value => value.IsStale);
        if (freshUsage)
            return failures.Count > 0 || staleUsage ? SnapshotStatus.SuccessPartial : SnapshotStatus.Success;
        if (staleUsage) return SnapshotStatus.Stale;
        if (values.Any(value => value.Kind == CapabilityKind.ProbeDiagnostic) && failures.Count == 0)
            return SnapshotStatus.ProbeOnly;
        if (failures.Count > 0) return QueryFailureClassifier.SnapshotStatusOf(failures[0].Status);
        if (plan.RequiresSelection) return SnapshotStatus.PermanentFailure;
        return SnapshotStatus.NoData;
    }

    private static RefreshReason ToRefreshReason(ScanReason reason) => reason switch
    {
        ScanReason.PageSaved => RefreshReason.PageSaved,
        ScanReason.ConfigurationChanged => RefreshReason.ConfigurationChanged,
        ScanReason.Startup => RefreshReason.FingerprintChanged,
        ScanReason.FingerprintChanged => RefreshReason.FingerprintChanged,
        ScanReason.ConsecutiveFailures => RefreshReason.ConsecutiveFailures,
        _ => RefreshReason.Manual,
    };

    private async Task<T> WithPageLock<T>(string pageId, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = _pageLocks.GetOrAdd(pageId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }

    private sealed record QueryAggregate(CapabilitySnapshot Snapshot, FailureInfo? Failure);
}
