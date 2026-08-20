using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>选择结果：当前方法 + 选择状态 + 有序候选链。</summary>
public sealed record SelectionResult(
    string? SelectedMethodId,
    CandidateStatus Status,
    IReadOnlyList<MethodCandidate> Ordered);

/// <summary>
/// 候选选择：按来源等级、凭据匹配、统计覆盖、数据新鲜度、置信度和固定优先级排序。
/// 套餐名称/planId 不参与排序。最高候选并列时不随机选择，标记 RequiresSelection。
/// </summary>
public static class CandidateSelector
{
    public static IReadOnlyList<MethodCandidate> Order(IEnumerable<MethodCandidate> candidates)
        => candidates
            .OrderBy(c => SourceRank(c.Method.SourceKind))
            .ThenBy(c => StabilityRank(c.Method.Stability))
            .ThenBy(c => c.Method.DefaultPriority)
            .ThenByDescending(c => c.Confidence)
            .ToList();

    /// <summary>
    /// 自动选择：只选唯一、可解释的最高候选；最高候选无法区分时进入 RequiresSelection。
    /// 无可用候选时返回 AuthRequired / NoReliableUsage，按已有关键状态提示。
    /// </summary>
    public static SelectionResult Select(IReadOnlyList<MethodCandidate> ordered)
    {
        var available = ordered.Where(c => c.IsAvailable).ToList();
        if (available.Count == 0)
        {
            var status = ordered.Any(c => c.Status == CandidateStatus.AuthRequired)
                ? CandidateStatus.AuthRequired
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
