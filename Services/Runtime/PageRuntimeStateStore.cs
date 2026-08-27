using System.Collections.Concurrent;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>
/// 进程内页面运行时状态。页面扫描、来源计划、快照和临时覆盖按 Page.Id 隔离，
/// 不把临时覆盖写入磁盘，也不把非活动页面投影到全局 UI。
/// </summary>
public sealed class PageRuntimeStateStore
{
    public sealed class State
    {
        public required string PageId { get; init; }
        public string? Fingerprint { get; set; }
        public ScanReport? Scan { get; set; }
        public CapabilitySourcePlan? Plan { get; set; }
        public CapabilitySnapshot? Snapshot { get; set; }
        public FailureInfo? LastFailure { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public string? TemporaryOverrideMethodId { get; set; }
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public State GetOrCreate(string pageId) => _states.GetOrAdd(pageId, id => new State { PageId = id });

    public bool TryGet(string pageId, out State state) => _states.TryGetValue(pageId, out state!);

    public bool TryGetSnapshot(string pageId, out CapabilitySnapshot snapshot)
    {
        if (_states.TryGetValue(pageId, out var state) && state.Snapshot is { } value)
        {
            snapshot = value;
            return true;
        }
        snapshot = null!;
        return false;
    }

    public bool TryGetScan(string pageId, out ScanReport report)
    {
        if (_states.TryGetValue(pageId, out var state) && state.Scan is { } value)
        {
            report = value;
            return true;
        }
        report = null!;
        return false;
    }

    public void SetTemporaryOverride(string pageId, string? methodId)
        => GetOrCreate(pageId).TemporaryOverrideMethodId = methodId;

    public void ClearTemporaryOverride(string pageId)
    {
        if (_states.TryGetValue(pageId, out var state)) state.TemporaryOverrideMethodId = null;
    }

    public string? TemporaryOverrideFor(string pageId)
        => _states.TryGetValue(pageId, out var state) ? state.TemporaryOverrideMethodId : null;

    public void Clear(string pageId)
        => _states.TryRemove(pageId, out _);
}
