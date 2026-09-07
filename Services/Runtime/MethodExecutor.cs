using System.Collections.Concurrent;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>执行单个查询方法，保留成功历史并隔离本次失败。</summary>
public sealed class MethodExecutor
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);
    private readonly MethodResultCache _cache;
    private readonly TimeProvider _time;
    private readonly PageCatalog? _catalog;
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new();

    private sealed class AttemptState
    {
        public int Failures;
        public bool RescanPending;
        public DateTimeOffset? RetryAt;
        public MethodQueryResult? LastResult;
        public MethodCandidate? LastCandidate;
        public DateTimeOffset? ScanRetryAt;
    }

    public bool ConsumeRescan(string pageId)
    {
        var pending = false;
        foreach (var pair in _attempts.Where(pair => pair.Key.StartsWith(pageId + "|", StringComparison.Ordinal)))
        {
            if (!pair.Value.RescanPending && !(pair.Value.ScanRetryAt <= _time.GetUtcNow())) continue;
            pair.Value.RescanPending = false;
            pair.Value.Failures = 0;
            pair.Value.ScanRetryAt = null;
            pending = true;
        }
        return pending;
    }

    public void InvalidatePage(string pageId)
    {
        foreach (var key in _attempts.Keys.Where(key => key.StartsWith(pageId + "|", StringComparison.Ordinal)))
            _attempts.TryRemove(key, out _);
        _cache.InvalidatePage(pageId);
    }

    public MethodExecutor(MethodResultCache cache, TimeProvider? time = null, PageCatalog? catalog = null)
    {
        _cache = cache;
        _time = time ?? TimeProvider.System;
        _catalog = catalog;
    }

    private void Publish(PageConfigRecord page, Action action)
    {
        if (_catalog is null) action();
        else _catalog.Publish(page, action);
    }

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, IQueryMethod method,
        ScanContext context, CancellationToken ct)
    {
        var descriptor = method.Describe();
        var key = MethodResultCache.MethodKey(page.Id, context.ConfigurationFingerprint,
            descriptor.MethodId, descriptor.ImplementationVersion);
        var state = _attempts.GetOrAdd(key, _ => new AttemptState());
        if (state.RetryAt > _time.GetUtcNow() && state.LastCandidate is { } cached)
            return cached;
        var candidate = await method.ScanAsync(page, context, ct);
        Publish(page, () =>
        {
        state.LastCandidate = candidate;
        if (candidate.Status == CandidateStatus.RateLimited)
        {
            state.RetryAt = candidate.Failure?.RetryAt ?? _time.GetUtcNow().AddMinutes(1);
            state.ScanRetryAt = state.RetryAt;
        }
        else if (candidate.Status == CandidateStatus.NetworkFailure)
            state.ScanRetryAt = _time.GetUtcNow().AddMinutes(1);
        else
            state.ScanRetryAt = null;
        });
        return candidate;
    }
    public async Task<MethodQueryResult> ExecuteAsync(
        PageConfigRecord page,
        IQueryMethod method,
        MethodCandidate candidate,
        string fingerprint,
        CancellationToken ct, bool forceRefresh = false)
    {
        var descriptor = method.Describe();
        var key = MethodResultCache.MethodKey(page.Id, fingerprint, descriptor.MethodId, descriptor.ImplementationVersion);
        var state = _attempts.GetOrAdd(key, _ => new AttemptState());
        if (state.LastResult is { } previous
            && (state.RetryAt > _time.GetUtcNow()
                || (!forceRefresh && previous.Failure is { } failure && RetryPolicy.IsTerminal(failure.Status))))
            return WithHistory(key, previous);
        if (!forceRefresh && _cache.TryGet(key, out var cached)
            && cached.HasLastSuccessfulSnapshot
            && cached.LastAttemptResult?.Failure is null
            && _time.GetUtcNow() - cached.CachedAt <= CacheTtl)
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
            catch (QueryTransportException ex)
            {
                result = FailedResult(ex.Status, ex.Message) with
                {
                    Failure = new FailureInfo(ex.Status, ex.Message, _time.GetUtcNow(), RetryAt: ex.RetryAt),
                };
            }
            catch (Exception ex)
            {
                Logger.LogException($"query {descriptor.MethodId}", ex);
                result = FailedResult(QueryFailureClassifier.StatusOf(ex), QueryFailureClassifier.ReasonOf(ex));
            }

            if (result is null || result.Failure is null || !RetryPolicy.ShouldRetry(result.Failure.Status, attempt))
                break;
            await Task.Delay(RetryPolicy.Backoff(attempt), _time, ct);
        }

        result ??= FailedResult(CandidateStatus.NetworkFailure, "查询未返回结果");
        Publish(page, () =>
        {
        state.LastResult = result;
        state.RetryAt = result.Failure?.Status == CandidateStatus.RateLimited
            ? result.Failure.RetryAt ?? _time.GetUtcNow().AddMinutes(1) : null;
        if (result.Failure?.Status == CandidateStatus.NetworkFailure)
        {
            state.Failures++;
            if (state.Failures >= RetryPolicy.RescanAfterConsecutiveFailures) state.RescanPending = true;
        }
        else if (result.Failure is null)
        {
            state.Failures = 0;
            state.RescanPending = false;
        }
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
        }
        });
        return IsSuccessfulResult(result) ? result : WithHistory(key, result);
    }

    private MethodQueryResult WithHistory(string key, MethodQueryResult result)
    {
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

    private MethodQueryResult FailedResult(CandidateStatus status, string reason)
    {
        var snapshotStatus = QueryFailureClassifier.SnapshotStatusOf(status);
        return new MethodQueryResult(
            Array.Empty<CapabilityValue>(),
            snapshotStatus,
            new FailureInfo(status, reason, _time.GetUtcNow()),
            _time.GetUtcNow());
    }

}
