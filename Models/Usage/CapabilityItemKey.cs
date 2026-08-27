namespace TokenConsumptionMonitoring.Models.Usage;

/// <summary>能力条目的稳定键：能力类型 + 来源身份 + 来源内条目键。</summary>
public readonly record struct CapabilityItemKey(
    CapabilityKind Capability,
    string SourceStableKey,
    string ItemKey)
{
    public override string ToString() => $"{Capability}|{SourceStableKey}|{ItemKey}";

    public static CapabilityItemKey For(CapabilityValue value)
        => new(value.Kind, value.Source.StableKey, ItemKeyOf(value));

    private static string ItemKeyOf(CapabilityValue value) => value switch
    {
        RollingWindowValue window => window.WindowKey,
        BalanceQuotaValue balance =>
            $"{balance.Currency}|{balance.Unit ?? "balance"}|{balance.Coverage.Scope ?? "unknown"}",
        ReportedUsageValue usage =>
            $"{CoverageKey(usage.Coverage)}|{string.Join(",", usage.Models.Select(m => m.Model).OrderBy(model => model, StringComparer.Ordinal))}",
        ReportedCostValue cost => $"{CoverageKey(cost.Coverage)}|{cost.Currency}",
        EstimatedCostValue cost => $"{CoverageKey(cost.Coverage)}|{cost.Currency}|{cost.PricingVersion}",
        ResponseUsageValue response => response.RequestId ?? CoverageKey(response.Coverage),
        ProbeDiagnosticValue => "probe",
        _ => value.Kind.ToString(),
    };

    private static string CoverageKey(Coverage coverage)
        => $"{coverage.Scope ?? "unknown"}|{coverage.Start?.UtcDateTime:o}|{coverage.End?.UtcDateTime:o}|{coverage.Granularity}";
}
