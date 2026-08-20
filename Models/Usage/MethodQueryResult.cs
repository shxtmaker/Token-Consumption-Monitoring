namespace TokenConsumptionMonitoring.Models.Usage;

/// <summary>查询结果：方法查询阶段返回的能力集合（统一为能力化快照，不暴露供应商专属 PageData）。</summary>
public sealed record MethodQueryResult(
    IReadOnlyList<CapabilityValue> Capabilities,
    SnapshotStatus Status,
    FailureInfo? Failure,
    DateTimeOffset FetchedAt)
{
    public static MethodQueryResult Empty(SnapshotStatus status, string reason) =>
        new(Array.Empty<CapabilityValue>(), status, new FailureInfo(CandidateStatus.NoReliableUsage, reason, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
}
