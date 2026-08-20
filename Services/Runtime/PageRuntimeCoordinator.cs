using System.Collections.Concurrent;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>
/// 页面运行时协调器：候选扫描、方法选择、能力查询、按能力回退、缓存与状态生成。
/// PageEngine 只保留页面生命周期，向本协调器委托刷新与重扫。
/// </summary>
public sealed class PageRuntimeCoordinator : IPageRuntimeCoordinator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    private readonly QueryMethodRegistry _registry;
    private readonly FingerprintBuilder _fingerprints;
    private readonly MethodStateStore _stateStore;
    private readonly MethodResultCache _cache;
    private readonly ZCodeUsageService _zcode;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pageLocks = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();

    public PageRuntimeCoordinator(
        QueryMethodRegistry registry,
        FingerprintBuilder fingerprints,
        MethodStateStore stateStore,
        MethodResultCache cache,
        ZCodeUsageService zcode)
    {
        _registry = registry;
        _fingerprints = fingerprints;
        _stateStore = stateStore;
        _cache = cache;
        _zcode = zcode;
    }

    public Task<PageRuntimeResult> RefreshAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
        => WithPageLock(page.Id, () => RefreshCoreAsync(page, reason, ct), ct);

    public async Task<ScanReport> RescanAsync(PageConfigRecord page, ScanReason reason, CancellationToken ct)
    {
        var result = await WithPageLock(page.Id, () => RescanAndQueryAsync(page, reason switch
        {
            ScanReason.PageSaved => RefreshReason.PageSaved,
            ScanReason.ConfigurationChanged => RefreshReason.ConfigurationChanged,
            ScanReason.Startup => RefreshReason.FingerprintChanged,
            ScanReason.FingerprintChanged => RefreshReason.FingerprintChanged,
            ScanReason.ConsecutiveFailures => RefreshReason.ConsecutiveFailures,
            _ => RefreshReason.Manual,
        }, ct), ct);
        _consecutiveFailures[page.Id] = 0;
        return result.Scan!;
    }

    public void SetTemporaryOverride(string pageId, string? methodId)
    {
        var state = _stateStore.Load(pageId) ?? new PageMethodState { PageId = pageId };
        state.TemporaryOverrideMethodId = methodId;
        _stateStore.Save(state);
        _cache.InvalidatePage(pageId);
    }

    // ---- 刷新/重扫 ----

    private async Task<PageRuntimeResult> RefreshCoreAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
    {
        var fingerprint = _fingerprints.Build(page, _zcode.DatabaseExists);
        var state = _stateStore.Load(page.Id);

        var needRescan = reason is RefreshReason.PageSaved
            or RefreshReason.ConfigurationChanged
            or RefreshReason.FingerprintChanged
            or RefreshReason.ConsecutiveFailures
            or RefreshReason.Manual
            || state is null
            || state.Fingerprint != fingerprint
            || state.SelectedMethodId is null
            || _registry.Find(state.SelectedMethodId) is null;

        if (needRescan)
            return await RescanAndQueryAsync(page, reason, ct);

        return await PollQueryAsync(page, state!, fingerprint, ct);
    }

    private async Task<PageRuntimeResult> RescanAndQueryAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
    {
        var fingerprint = _fingerprints.Build(page, _zcode.DatabaseExists);
        var context = new ScanContext
        {
            Page = page,
            ConfigurationFingerprint = fingerprint,
            Credentials = new CredentialResolver(page),
            CancellationToken = ct,
        };

        // 1) 扫描注册表中的全部方法（远程 + 本地记录）；每个方法独立决定启用条件
        var candidates = new List<MethodCandidate>(_registry.Methods.Count);
        foreach (var method in _registry.Methods)
        {
            try
            {
                candidates.Add(await method.ScanAsync(page, context, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogException($"scan {method.Describe().MethodId}", ex);
                candidates.Add(MethodSupport.NotAvailable(method.Describe(), CandidateStatus.NetworkFailure, "扫描异常"));
            }
        }

        // 2) 排序 + 选择
        var ordered = CandidateSelector.Order(candidates);
        var selection = CandidateSelector.Select(ordered);
        var prior = _stateStore.Load(page.Id);

        // 3) 临时覆盖只影响当前运行时：仅当所选方法有效且已出现在候选链时应用
        var effectiveMethodId = selection.SelectedMethodId;
        if (prior?.TemporaryOverrideMethodId is { } overrideId
            && _registry.Find(overrideId) is not null
            && ordered.Any(c => c.Method.MethodId == overrideId))
        {
            effectiveMethodId = overrideId;
        }

        var state = new PageMethodState
        {
            PageId = page.Id,
            Fingerprint = fingerprint,
            ScannedAt = DateTimeOffset.UtcNow,
            Candidates = ordered.ToList(),
            SelectedMethodId = effectiveMethodId,
            SelectionStatus = selection.Status,
            TemporaryOverrideMethodId = prior?.TemporaryOverrideMethodId,
        };
        _stateStore.Save(state);

        if (prior?.Fingerprint != fingerprint)
            _cache.InvalidatePage(page.Id);

        // 4) 查询已选方法（按能力回退）
        var snapshot = await QuerySelectedAsync(page, state, fingerprint, ct);

        var report = new ScanReport(page.Id, fingerprint, ordered, effectiveMethodId, selection.Status, state.ScannedAt);
        _consecutiveFailures[page.Id] = 0;
        var authClass = ordered.FirstOrDefault(c => c.Status == CandidateStatus.AuthRequired)?.Method.CredentialClass;
        return new PageRuntimeResult(page.Id, snapshot, report, FailureFrom(snapshot), authClass);
    }

    private async Task<PageRuntimeResult> PollQueryAsync(PageConfigRecord page, PageMethodState state, string fingerprint, CancellationToken ct)
    {
        var snapshot = await QuerySelectedAsync(page, state, fingerprint, ct);
        var hasUsable = snapshot.Capabilities.Any(c => c.Kind != CapabilityKind.ProbeDiagnostic);
        var fail = FailureFrom(snapshot);

        if (!hasUsable && fail is not null)
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
        var authClass = state.Candidates.FirstOrDefault(c => c.Status == CandidateStatus.AuthRequired)?.Method.CredentialClass;
        return new PageRuntimeResult(page.Id, snapshot, null, fail, authClass);
    }

    // ---- 查询与按能力回退 ----

    /// <summary>
    /// 查询当前方法，并对其余能力按候选链顺序回退（同一能力只选一份事实，不跨来源相加）。
    /// 首选来源能力缺失不是整体失败；失败原因单独保留。
    /// </summary>
    private async Task<CapabilitySnapshot> QuerySelectedAsync(PageConfigRecord page, PageMethodState state, string fingerprint, CancellationToken ct)
    {
        var primaryId = state.TemporaryOverrideMethodId ?? state.SelectedMethodId;
        var ordered = state.Candidates;
        var primaryCandidate = ordered.FirstOrDefault(c => c.Method.MethodId == primaryId);

        var values = new List<CapabilityValue>();
        var provided = new HashSet<CapabilityKind>();
        FailureInfo? primaryFailure = null;
        var anyMissingPrimary = false;

        if (primaryCandidate is not null && _registry.Find(primaryCandidate.Method.MethodId) is { } primaryMethod)
        {
            var (caps, _, failure) = await QueryMethodAsync(page, primaryMethod, primaryCandidate, state, fingerprint, ct);
            AddCaps(values, provided, caps);
            if (failure is not null)
            {
                // 报告能力缺失：主来源失败但已有回退覆盖时记 partial；否则保留失败
                if (caps.All(c => c.Kind == CapabilityKind.ProbeDiagnostic))
                    primaryFailure = failure;
                else
                    anyMissingPrimary = true;
            }
        }

        foreach (var candidate in ordered.Where(c => c.IsAvailable && c.Method.MethodId != primaryId))
        {
            var missing = candidate.Method.Capabilities.Where(k => !provided.Contains(k) && k != CapabilityKind.ProbeDiagnostic).ToList();
            if (missing.Count == 0) continue;
            if (_registry.Find(candidate.Method.MethodId) is not { } method) continue;

            var (caps, _, failure) = await QueryMethodAsync(page, method, candidate, state, fingerprint, ct);
            var fresh = caps.Where(c =>
            {
                if (c.Kind == CapabilityKind.ProbeDiagnostic) return false;
                return !provided.Contains(c.Kind);
            }).ToList();
            AddCaps(values, provided, fresh);
            if (failure is not null && fresh.Count == 0)
                anyMissingPrimary = true;   // 该能力回退时也失败 → 部分失败
        }

        var status = ResolveSnapshotStatus(values, provided, primaryFailure, anyMissingPrimary);
        return new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata(page.Id, fingerprint, DateTimeOffset.UtcNow, primaryId, RefreshReason.Poll),
            Status = status,
            Capabilities = values,
        };
    }

    /// <summary>对单个方法执行查询：缓存命中优先；失败按策略重试；仍失败保留旧缓存并标记 stale。</summary>
    private async Task<(IReadOnlyList<CapabilityValue> Capabilities, SnapshotStatus Status, FailureInfo? Failure)> QueryMethodAsync(
        PageConfigRecord page, IQueryMethod method, MethodCandidate candidate, PageMethodState state, string fingerprint, CancellationToken ct)
    {
        var descriptor = method.Describe();
        var key = MethodResultCache.MethodKey(page.Id, fingerprint, descriptor.MethodId, descriptor.ImplementationVersion);
        if (_cache.TryGet(key, out var cached) && !cached.IsStale(CacheTtl))
            return (cached.Snapshot.Capabilities, cached.Snapshot.Status, FailureFrom(cached.Snapshot));

        MethodQueryResult? result = null;
        FailureInfo? lastFailure = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                result = await method.QueryAsync(page, candidate, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogException($"query {descriptor.MethodId}", ex);
                lastFailure = new FailureInfo(CandidateStatus.NetworkFailure, ex.Message, DateTimeOffset.UtcNow);
                result = null;
            }

            if (result is { Failure: null }) break;
            lastFailure = result?.Failure ?? lastFailure;
            var status = result?.Failure?.Status ?? CandidateStatus.NetworkFailure;
            if (RetryPolicy.ShouldRetry(status, attempt))
            {
                await Task.Delay(RetryPolicy.Backoff(attempt), ct);
                continue;
            }
            break;
        }

        if (result is not null)
        {
            var snap = new CapabilitySnapshot
            {
                Metadata = new SnapshotMetadata(page.Id, fingerprint, result.FetchedAt, descriptor.MethodId, RefreshReason.Poll),
                Status = result.Status,
                Capabilities = result.Capabilities,
            };
            _cache.Put(key, snap);
            return (snap.Capabilities, snap.Status, result.Failure);
        }

        // 失败：保留旧缓存并标记 stale（不触发新告警）
        if (_cache.TryGet(key, out var stale))
        {
            var staleSnap = stale.Snapshot with { Status = SnapshotStatus.Stale };
            return (staleSnap.Capabilities, SnapshotStatus.Stale, lastFailure);
        }
        var status2 = MapFailure(lastFailure?.Status ?? CandidateStatus.NetworkFailure);
        return (Array.Empty<CapabilityValue>(), status2, lastFailure);
    }

    private static void AddCaps(List<CapabilityValue> values, HashSet<CapabilityKind> provided, IEnumerable<CapabilityValue> caps)
    {
        foreach (var cap in caps)
        {
            if (provided.Add(cap.Kind)) values.Add(cap);
        }
    }

    private static SnapshotStatus ResolveSnapshotStatus(
        List<CapabilityValue> values, HashSet<CapabilityKind> provided, FailureInfo? primaryFailure, bool anyMissing)
    {
        var hasUsage = values.Any(c => c.Kind != CapabilityKind.ProbeDiagnostic);
        if (hasUsage) return anyMissing || primaryFailure is not null ? SnapshotStatus.SuccessPartial : SnapshotStatus.Success;
        if (values.Any(c => c.Kind == CapabilityKind.ProbeDiagnostic)) return SnapshotStatus.ProbeOnly;
        return primaryFailure is not null ? MapFailure(primaryFailure.Status) : SnapshotStatus.NoData;
    }

    private static SnapshotStatus MapFailure(CandidateStatus status) => status switch
    {
        CandidateStatus.AuthRequired => SnapshotStatus.AuthRequired,
        CandidateStatus.RateLimited => SnapshotStatus.TemporaryFailure,
        CandidateStatus.NetworkFailure => SnapshotStatus.TemporaryFailure,
        CandidateStatus.SchemaMismatch => SnapshotStatus.PermanentFailure,
        CandidateStatus.Forbidden => SnapshotStatus.AuthRequired,
        CandidateStatus.Unsupported => SnapshotStatus.PermanentFailure,
        CandidateStatus.NoReliableUsage => SnapshotStatus.PermanentFailure,
        CandidateStatus.RequiresSelection => SnapshotStatus.PermanentFailure,
        CandidateStatus.Stale => SnapshotStatus.Stale,
        _ => SnapshotStatus.NoData,
    };

    private static FailureInfo? FailureFrom(CapabilitySnapshot snapshot) => snapshot.Status switch
    {
        SnapshotStatus.AuthRequired => new FailureInfo(CandidateStatus.AuthRequired, "需要鉴权/凭据", DateTimeOffset.UtcNow),
        SnapshotStatus.TemporaryFailure => new FailureInfo(CandidateStatus.NetworkFailure, "临时失败（可重试）", DateTimeOffset.UtcNow),
        SnapshotStatus.PermanentFailure => new FailureInfo(CandidateStatus.NoReliableUsage, "无可用用量来源", DateTimeOffset.UtcNow),
        _ => null,
    };

    private async Task<T> WithPageLock<T>(string pageId, Func<Task<T>> action, CancellationToken ct)
    {
        var sl = _pageLocks.GetOrAdd(pageId, _ => new SemaphoreSlim(1, 1));
        await sl.WaitAsync(ct);
        try { return await action(); }
        finally { sl.Release(); }
    }
}
