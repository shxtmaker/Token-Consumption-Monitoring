using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>候选构造助手：统一生成可解释的候选状态与失败信息（供方法与测试使用）。</summary>
public static class MethodSupport
{
    public const string ImplementationVersion = "1.0.0";

    public static MethodCandidate Available(
        QueryMethodDescriptor d,
        CredentialScope scope,
        Coverage coverage,
        IEnumerable<DetectionEvidence> evidence,
        SourceIdentity? source = null,
        int confidence = 85)
        => new(d, CandidateStatus.Available, confidence, source, scope, coverage, evidence.ToList(), null);

    public static MethodCandidate AuthRequired(QueryMethodDescriptor d, string reason, params DetectionEvidence[] evidence)
        => NotAvailable(d, CandidateStatus.AuthRequired, reason, 0, evidence);

    public static MethodCandidate NotAvailable(
        QueryMethodDescriptor d,
        CandidateStatus status,
        string reason,
        int confidence = 0,
        params DetectionEvidence[] evidence)
        => new(d, status, confidence, null, null, Coverage.Unknown, evidence.ToList(),
            new FailureInfo(status, reason, DateTimeOffset.UtcNow));
}
