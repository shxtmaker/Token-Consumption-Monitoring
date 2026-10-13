namespace TokenConsumptionMonitoring.Models;

/// <summary>官网整周期参考额度。它不属于账户余额，不参与实际用量、来源选择或告警。</summary>
public sealed record ModelQuotaReference(
    string ModelId, string ModelName, string PlanId, string PlanName,
    decimal? MonthlyUsd, bool IsFree, string Note, string SourceUrl);

public sealed record ModelQuotaCatalog(
    string Provider, DateTimeOffset FetchedAt, string Version,
    IReadOnlyList<ModelQuotaReference> Entries, bool IsStale = false, string? Warning = null,
    IReadOnlyList<ModelQuotaConversionRule>? ConversionRules = null);

/// <summary>公开定价规则。倍率将账户共享池单位换为指定模型的美元用量价值。</summary>
public sealed record ModelQuotaConversionRule(string ModelId, string PlanId, string AccountPlanId,
    decimal UsdPerPoolUnit, decimal? StandardMonthlyPool, DateTimeOffset ValidUntil, string SourceUrl);

public sealed record QuotaReferenceChoice(string Id, string Name);
