using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>一个能力槽的候选来源与最终选择。</summary>
public sealed record CapabilitySelection(
    CapabilityKind Capability,
    IReadOnlyList<MethodCandidate> Candidates,
    MethodCandidate? Selected,
    CandidateStatus Status)
{
    public bool RequiresSelection => Status == CandidateStatus.RequiresSelection;
}

/// <summary>
/// 扫描产生的能力来源计划。
/// 每个能力槽只选择一个来源，但选中方法返回的全部合法条目都由协调器保留。
/// </summary>
public sealed class CapabilitySourcePlan
{
    private readonly IReadOnlyDictionary<CapabilityKind, CapabilitySelection> _selections;

    private CapabilitySourcePlan(IReadOnlyDictionary<CapabilityKind, CapabilitySelection> selections)
        => _selections = selections;

    public IReadOnlyDictionary<CapabilityKind, CapabilitySelection> Selections => _selections;

    public IReadOnlyDictionary<CapabilityKind, MethodCandidate> SelectedByCapability =>
        _selections
            .Where(pair => pair.Value.Selected is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Selected!);

    public IReadOnlyList<MethodCandidate> SelectedCandidates =>
        SelectedByCapability.Values
            .GroupBy(candidate => candidate.Method.MethodId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

    public IReadOnlyDictionary<CapabilityKind, string> SelectedMethodIds =>
        SelectedByCapability.ToDictionary(pair => pair.Key, pair => pair.Value.Method.MethodId);

    public string? PrimaryMethodId => SelectedCandidates.FirstOrDefault()?.Method.MethodId;

    public bool RequiresSelection => _selections.Values.Any(selection => selection.RequiresSelection);

    public CandidateStatus OverallStatus
    {
        get
        {
            if (RequiresSelection) return CandidateStatus.RequiresSelection;
            if (SelectedByCapability.Count > 0) return CandidateStatus.Available;
            var statuses = _selections.Values.Select(selection => selection.Status).ToList();
            if (statuses.Contains(CandidateStatus.AuthRequired)) return CandidateStatus.AuthRequired;
            if (statuses.Contains(CandidateStatus.Forbidden)) return CandidateStatus.Forbidden;
            if (statuses.Contains(CandidateStatus.RateLimited)) return CandidateStatus.RateLimited;
            if (statuses.Contains(CandidateStatus.NetworkFailure)) return CandidateStatus.NetworkFailure;
            if (statuses.Contains(CandidateStatus.SchemaMismatch)) return CandidateStatus.SchemaMismatch;
            if (statuses.Contains(CandidateStatus.NoReliableUsage)) return CandidateStatus.NoReliableUsage;
            return CandidateStatus.Unsupported;
        }
    }

    public bool TryGet(CapabilityKind capability, out MethodCandidate candidate)
        => SelectedByCapability.TryGetValue(capability, out candidate!);

    /// <summary>按候选链为每个能力槽选源，可用 preferred 时保持已确认的来源选择。</summary>
    public static CapabilitySourcePlan Build(
        IEnumerable<MethodCandidate> candidates,
        IReadOnlyDictionary<CapabilityKind, string>? preferredMethodIds = null,
        string? overrideMethodId = null)
    {
        var ordered = CandidateSelector.Order(candidates);
        var capabilities = ordered
            .SelectMany(candidate => candidate.Method.Capabilities)
            .Distinct()
            .ToList();
        var selections = new Dictionary<CapabilityKind, CapabilitySelection>();

        foreach (var capability in capabilities)
        {
            var capabilityCandidates = ordered
                .Where(candidate => candidate.Method.Capabilities.Contains(capability))
                .ToList();
            var available = capabilityCandidates.Where(candidate => candidate.IsAvailable).ToList();
            MethodCandidate? selected = null;
            CandidateStatus status;

            if (overrideMethodId is not null)
                selected = available.FirstOrDefault(candidate => candidate.Method.MethodId == overrideMethodId);

            if (selected is null && preferredMethodIds?.TryGetValue(capability, out var preferred) == true)
                selected = available.FirstOrDefault(candidate => candidate.Method.MethodId == preferred);

            if (selected is not null)
            {
                status = CandidateStatus.Available;
            }
            else
            {
                var selection = CandidateSelector.Select(available.Count > 0 ? available : capabilityCandidates);
                selected = selection.SelectedMethodId is { } selectedId
                    ? available.FirstOrDefault(candidate => candidate.Method.MethodId == selectedId)
                    : null;
                status = selection.Status;
            }

            selections[capability] = new CapabilitySelection(capability, capabilityCandidates, selected, status);
        }

        return new CapabilitySourcePlan(selections);
    }
}
