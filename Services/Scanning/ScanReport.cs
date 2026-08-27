using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>扫描报告：候选链、能力来源计划、指纹与扫描时间。</summary>
public sealed record ScanReport(
    string PageId,
    string Fingerprint,
    IReadOnlyList<MethodCandidate> Candidates,
    CapabilitySourcePlan Plan,
    DateTimeOffset ScannedAt)
{
    /// <summary>诊断兼容入口；选择语义以 Plan.SelectedMethodIds 为准。</summary>
    public string? SelectedMethodId => Plan.PrimaryMethodId;

    public IReadOnlyDictionary<CapabilityKind, string> SelectedMethodIds => Plan.SelectedMethodIds;

    public CandidateStatus SelectionStatus => Plan.OverallStatus;

    public bool RequiresSelection => Plan.RequiresSelection;

    public ScanReport(
        string pageId,
        string fingerprint,
        IReadOnlyList<MethodCandidate> candidates,
        string? selectedMethodId,
        CandidateStatus selectionStatus,
        DateTimeOffset scannedAt)
        : this(pageId, fingerprint, candidates, CapabilitySourcePlan.Build(
            candidates,
            selectedMethodId is null
                ? null
                : candidates
                    .SelectMany(candidate => candidate.Method.Capabilities.Select(capability => (capability, candidate.Method.MethodId)))
                    .GroupBy(pair => pair.capability)
                    .ToDictionary(group => group.Key, group => group.First().MethodId)), scannedAt)
    {
    }
}
