using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>选择结果：当前方法 + 选择状态 + 有序候选链。</summary>
public sealed record SelectionResult(
    string? SelectedMethodId,
    CandidateStatus Status,
    IReadOnlyList<MethodCandidate> Ordered);

/// <summary>
/// 候选选择：按来源稳定性、来源类型、固定优先级和置信度排序。
/// 计划信息不参与排序。私有兼容来源始终低于本地回退来源；最高候选并列时不随机选择，标记 RequiresSelection。
/// </summary>
public static class CandidateSelector
{
    public static IReadOnlyList<MethodCandidate> Order(IEnumerable<MethodCandidate> candidates)
        => candidates
            .OrderBy(c => StabilityRank(c.Method.Stability))
            .ThenBy(c => SourceRank(c.Method.SourceKind))
            .ThenBy(c => c.Method.DefaultPriority)
            .ThenByDescending(c => c.Confidence)
            .ToList();

    /// <summary>
    /// 自动选择：只选唯一、可解释的最高候选；最高候选无法区分时进入 RequiresSelection。
    /// 无可用候选时返回最有解释力的失败状态。
    /// </summary>
    public static SelectionResult Select(IReadOnlyList<MethodCandidate> ordered)
    {
        var available = ordered.Where(c => c.IsAvailable).ToList();
        if (available.Count == 0)
        {
            var status = ordered.Any(c => c.Status == CandidateStatus.AuthRequired)
                ? CandidateStatus.AuthRequired
                : ordered.Any(c => c.Status == CandidateStatus.Forbidden)
                    ? CandidateStatus.Forbidden
                    : ordered.Any(c => c.Status == CandidateStatus.RateLimited)
                        ? CandidateStatus.RateLimited
                        : ordered.Any(c => c.Status == CandidateStatus.NetworkFailure)
                            ? CandidateStatus.NetworkFailure
                            : ordered.Any(c => c.Status == CandidateStatus.SchemaMismatch)
                                ? CandidateStatus.SchemaMismatch
                                : ordered.Any(c => c.Status == CandidateStatus.NoReliableUsage)
                                    ? CandidateStatus.NoReliableUsage
                                    : CandidateStatus.Unsupported;
            return new SelectionResult(null, status, ordered);
        }

        var top = available[0];
        var tied = available
            .Where(c => SourceRank(c.Method.SourceKind) == SourceRank(top.Method.SourceKind)
                        && StabilityRank(c.Method.Stability) == StabilityRank(top.Method.Stability)
                        && c.Method.DefaultPriority == top.Method.DefaultPriority
                        && c.Confidence == top.Confidence)
            .ToList();

        if (tied.Count > 1)
            return new SelectionResult(null, CandidateStatus.RequiresSelection, ordered);

        return new SelectionResult(top.Method.MethodId, CandidateStatus.Available, ordered);
    }

    /// <summary>来源等级排序（低优先在前）。</summary>
    private static int SourceRank(SourceKind k) => k switch
    {
        SourceKind.RemoteOfficialStats => 0,
        SourceKind.AllowanceOrBalance => 1,
        SourceKind.RollingWindowSnapshot => 1,
        SourceKind.LocalRecord => 2,
        SourceKind.ResponseUsage => 3,
        SourceKind.ConsoleOrPrivateUI => 4,
        SourceKind.Probe => 5,
        _ => 6,
    };

    private static int StabilityRank(SourceStability s) => s switch
    {
        SourceStability.OfficialStable => 0,
        SourceStability.OfficialConditional => 1,
        SourceStability.LocalFallback => 2,
        SourceStability.PrivateCompat => 3,
        SourceStability.ProbeOnly => 4,
        _ => 5,
    };
}
