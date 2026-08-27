using System.ComponentModel;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.UI.Diagnostics;

/// <summary>候选方法行：方法标识、来源等级、凭据范围、置信度、状态与证据（诊断工作台中栏）。</summary>
public sealed class MethodCandidateViewModel : INotifyPropertyChanged
{
    public string MethodId { get; }
    public string StabilityLabel { get; }
    public string ScopeLabel { get; }
    public int Confidence { get; }
    public CandidateStatus StatusCandidate { get; }
    public string StatusLabel { get; }
    public string FailureText { get; }
    public string EvidenceText { get; }
    public string CapabilitiesLabel { get; }
    public bool IsAvailable { get; }
    public string StatusColorHex { get; }

    public bool IsCurrent { get; private set; }

    public MethodCandidateViewModel(MethodCandidate candidate, string effectiveMethodId)
        : this(candidate, new HashSet<string>(new[] { effectiveMethodId }, StringComparer.Ordinal))
    {
    }

    public MethodCandidateViewModel(MethodCandidate candidate, IReadOnlySet<string> effectiveMethodIds)
    {
        MethodId = candidate.Method.MethodId;
        Confidence = candidate.Confidence;
        StatusCandidate = candidate.Status;
        IsAvailable = candidate.IsAvailable;
        StatusLabel = StatusText(candidate.Status, candidate.IsAvailable);
        FailureText = candidate.Failure?.Reason ?? "";
        EvidenceText = string.Join(" · ", candidate.Evidence.Select(e => e.Detail));
        CapabilitiesLabel = string.Join(",", candidate.Method.Capabilities.Select(AbilityText));
        StabilityLabel = StabilityText(candidate.Method.Stability);
        ScopeLabel = candidate.CredentialScope?.Describe() ?? candidate.Method.CredentialClass.ToString();
        StatusColorHex = ColorFor(candidate.Status);
        IsCurrent = effectiveMethodIds.Contains(candidate.Method.MethodId);
    }

    public void RefreshCurrent(string effectiveMethodId)
    {
        IsCurrent = MethodId == effectiveMethodId;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
    }

    public static string StabilityText(SourceStability s) => s switch
    {
        SourceStability.OfficialStable => "官方稳定",
        SourceStability.OfficialConditional => "官方条件",
        SourceStability.LocalFallback => "本地备选",
        SourceStability.PrivateCompat => "私有兼容",
        SourceStability.ProbeOnly => "仅探测",
        _ => "未知",
    };

    public static string StatusText(CandidateStatus s, bool isAvailable) => s switch
    {
        CandidateStatus.Available => "可用",
        CandidateStatus.AuthRequired => "需要凭据/权限",
        CandidateStatus.Forbidden => "权限不足 (403)",
        CandidateStatus.RateLimited => "限流 (429)",
        CandidateStatus.NetworkFailure => "网络失败",
        CandidateStatus.SchemaMismatch => "结构不匹配",
        CandidateStatus.NoReliableUsage => "无可靠用量来源",
        CandidateStatus.Unsupported => "不支持",
        CandidateStatus.RequiresSelection => "需要选择",
        CandidateStatus.Stale => "数据过期",
        _ => isAvailable ? "可用" : "未知",
    };

    public static string AbilityText(CapabilityKind k) => k switch
    {
        CapabilityKind.ReportedUsage => "用量",
        CapabilityKind.ReportedCost => "费用",
        CapabilityKind.EstimatedCost => "估成本",
        CapabilityKind.BalanceOrQuota => "余额/额度",
        CapabilityKind.RollingWindow => "窗口",
        CapabilityKind.ResponseUsage => "遥测",
        CapabilityKind.ProbeDiagnostic => "探测",
        _ => k.ToString(),
    };

    private static string ColorFor(CandidateStatus s) => s switch
    {
        CandidateStatus.Available => "#3FB95F",
        CandidateStatus.RequiresSelection => "#E0A52B",
        CandidateStatus.AuthRequired => "#E0A52B",
        CandidateStatus.Forbidden => "#E54848",
        CandidateStatus.RateLimited => "#E54848",
        CandidateStatus.NetworkFailure => "#E54848",
        CandidateStatus.SchemaMismatch => "#E54848",
        CandidateStatus.NoReliableUsage => "#9A9A9A",
        CandidateStatus.Unsupported => "#6B6B6B",
        CandidateStatus.Stale => "#9A9A9A",
        _ => "#6B6B6B",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
