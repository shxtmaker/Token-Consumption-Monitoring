namespace TokenConsumptionMonitoring.Models.Usage;

/// <summary>查询结果：方法查询阶段返回的能力集合，不暴露供应商专属响应模型。</summary>
public sealed record MethodQueryResult(
    IReadOnlyList<CapabilityValue> Capabilities,
    SnapshotStatus Status,
    FailureInfo? Failure,
    DateTimeOffset FetchedAt)
{
    public static MethodQueryResult Empty(SnapshotStatus status, string reason) =>
        new(Array.Empty<CapabilityValue>(), status,
            new FailureInfo(FailureStatusOf(status), reason, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

    private static CandidateStatus FailureStatusOf(SnapshotStatus status) => status switch
    {
        SnapshotStatus.AuthRequired => CandidateStatus.AuthRequired,
        SnapshotStatus.Forbidden => CandidateStatus.Forbidden,
        SnapshotStatus.RateLimited => CandidateStatus.RateLimited,
        SnapshotStatus.TemporaryFailure => CandidateStatus.NetworkFailure,
        SnapshotStatus.SchemaMismatch => CandidateStatus.SchemaMismatch,
        SnapshotStatus.Stale => CandidateStatus.Stale,
        _ => CandidateStatus.NoReliableUsage,
    };
}
