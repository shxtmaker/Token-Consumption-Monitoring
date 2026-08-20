using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>扫描报告：候选链、选择结果、指纹与扫描时间。</summary>
public sealed record ScanReport(
    string PageId,
    string Fingerprint,
    IReadOnlyList<MethodCandidate> Candidates,
    string? SelectedMethodId,
    CandidateStatus SelectionStatus,
    DateTimeOffset ScannedAt)
{
    public bool RequiresSelection => SelectionStatus == CandidateStatus.RequiresSelection;
}
