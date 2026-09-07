using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>能力值与失败事实到页面状态的纯决策。</summary>
public static class SnapshotPolicy
{
    public static SnapshotStatus Resolve(
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
        if (PrimaryFailure(failures) is { } failure) return QueryFailureClassifier.SnapshotStatusOf(failure.Status);
        if (plan.RequiresSelection) return SnapshotStatus.PermanentFailure;
        return SnapshotStatus.NoData;
    }

    public static FailureInfo? PrimaryFailure(IEnumerable<FailureInfo> failures)
        => failures.OrderBy(failure => failure.Status switch
        {
            CandidateStatus.RequiresSelection => 0,
            CandidateStatus.AuthRequired => 1,
            CandidateStatus.Forbidden => 2,
            CandidateStatus.RateLimited => 3,
            CandidateStatus.SchemaMismatch => 4,
            CandidateStatus.NetworkFailure => 5,
            _ => 6,
        }).FirstOrDefault();

}
