namespace TokenConsumptionMonitoring.Models.Usage;

/// <summary>模型用量明细行（名称 + tokens + 可选费用；细分 token 保留在数据接口）。</summary>
public sealed record ModelUsageRow(
    string Model,
    long Tokens,
    decimal? Cost = null,
    string? Currency = null,
    TokenBreakdown? Breakdown = null);

/// <summary>细分 token（输入/输出/缓存命中/未命中）；保留在数据接口，当前界面不展示。</summary>
public sealed record TokenBreakdown(
    long InputTokens,
    long OutputTokens,
    long CacheHitTokens,
    long CacheMissTokens);

/// <summary>
/// 能力值基类：每项能力带 source identity、credential scope、coverage、fetched at、
/// granularity、freshness/stale、confidence 与 private/estimated 标记。
/// JSON 采用多态（type discriminator）以支持快照序列化/反序列化。
/// </summary>
[System.Text.Json.Serialization.JsonPolymorphic(
    TypeDiscriminatorPropertyName = "$capability",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor)]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ReportedUsageValue), "reported-usage")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ReportedCostValue), "reported-cost")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(EstimatedCostValue), "estimated-cost")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(BalanceQuotaValue), "balance-quota")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RollingWindowValue), "rolling-window")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ResponseUsageValue), "response-usage")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ProbeDiagnosticValue), "probe-diagnostic")]
public abstract record CapabilityValue(
    CapabilityKind Kind,
    SourceIdentity Source,
    CredentialScope CredentialScope,
    Coverage Coverage,
    DateTimeOffset FetchedAt,
    double Confidence,
    bool IsPrivate,
    bool IsEstimated,
    DateTimeOffset? ExpiresAt = null)
{
    /// <summary>快照是否过期（仅影响展示标记，不触发新告警）。</summary>
    public bool IsStale(DateTimeOffset now) => ExpiresAt is { } e && now > e;

    public TimeSpan? Age(DateTimeOffset now) => now - FetchedAt;
}

/// <summary>报告用量：远程官方统计/网关统计/本地记录直接报告的 token、请求（用量事实，不由价格反推）。</summary>
public sealed record ReportedUsageValue(
    CapabilityKind Kind,
    SourceIdentity Source,
    CredentialScope CredentialScope,
    Coverage Coverage,
    DateTimeOffset FetchedAt,
    double Confidence,
    bool IsPrivate,
    bool IsEstimated,
    long TotalTokens,
    long TotalRequests,
    IReadOnlyList<ModelUsageRow> Models,
    DateTimeOffset? ExpiresAt = null)
    : CapabilityValue(Kind, Source, CredentialScope, Coverage, FetchedAt, Confidence, IsPrivate, IsEstimated, ExpiresAt);

/// <summary>服务方报告的实际费用（报告费用可告警，估算成本不可）。</summary>
public sealed record ReportedCostValue(
    CapabilityKind Kind,
    SourceIdentity Source,
    CredentialScope CredentialScope,
    Coverage Coverage,
    DateTimeOffset FetchedAt,
    double Confidence,
    bool IsPrivate,
    bool IsEstimated,
    decimal Amount,
    string Currency,
    DateTimeOffset? ExpiresAt = null)
    : CapabilityValue(Kind, Source, CredentialScope, Coverage, FetchedAt, Confidence, IsPrivate, IsEstimated, ExpiresAt);

/// <summary>估算成本：定价快照推导的金额；标记 estimated、币种、定价来源与版本；默认不展示不告警。</summary>
public sealed record EstimatedCostValue(
    CapabilityKind Kind,
    SourceIdentity Source,
    CredentialScope CredentialScope,
    Coverage Coverage,
    DateTimeOffset FetchedAt,
    double Confidence,
    bool IsPrivate,
    bool IsEstimated,
    decimal Amount,
    string Currency,
    string PricingSource,
    string PricingVersion,
    DateTimeOffset? ExpiresAt = null)
    : CapabilityValue(Kind, Source, CredentialScope, Coverage, FetchedAt, Confidence, IsPrivate, IsEstimated, ExpiresAt);

/// <summary>余额/credits/剩余额度：账务状态，不从 token 或估算成本推导，不与其他来源同名额度相加。</summary>
public sealed record BalanceQuotaValue(
    CapabilityKind Kind,
    SourceIdentity Source,
    CredentialScope CredentialScope,
    Coverage Coverage,
    DateTimeOffset FetchedAt,
    double Confidence,
    bool IsPrivate,
    bool IsEstimated,
    decimal? Balance,
    decimal? Used,
    decimal? Limit,
    decimal? Remaining,
    string Currency,
    string? Unit,
    DateTimeOffset? ExpiresAt = null)
    : CapabilityValue(Kind, Source, CredentialScope, Coverage, FetchedAt, Confidence, IsPrivate, IsEstimated, ExpiresAt)
{
    /// <summary>是否存在上限且剩余值明确（余额告警的必要边界）。</summary>
    public bool CanEvaluateLimit(WarnThresholdSet thresholds) =>
        Limit is { } limit && Remaining is { } remaining && thresholds != null && (thresholds.WarnPercent > 0 || thresholds.CriticalPercent > 0);
}

/// <summary>滚动/周期窗口：used、limit、remaining、percent、reset 时间。percent 优先由绝对值推导。</summary>
public sealed record RollingWindowValue(
    CapabilityKind Kind,
    SourceIdentity Source,
    CredentialScope CredentialScope,
    Coverage Coverage,
    DateTimeOffset FetchedAt,
    double Confidence,
    bool IsPrivate,
    bool IsEstimated,
    string WindowKey,
    string WindowName,
    string? Status,
    long? Used,
    long? Limit,
    long? Remaining,
    int? Percent,
    DateTimeOffset? ResetsAt,
    string? Unit,
    DateTimeOffset? ExpiresAt = null)
    : CapabilityValue(Kind, Source, CredentialScope, Coverage, FetchedAt, Confidence, IsPrivate, IsEstimated, ExpiresAt)
{
    public bool HasData => Percent is not null || Used is not null;

    /// <summary>百分比：绝对优先，其次界面推导，接口提供原始百分比时保留。</summary>
    public int EffectivePercent()
    {
        if (Percent is { } p && p >= 0) return p;
        if (Limit is { } l && l > 0 && Remaining is { } r) return (int)Math.Round(Math.Max(0, Math.Min(100, (double)(l - r) / l * 100)));
        return -1;
    }
}

/// <summary>请求响应/本地遥测单次用量：不能冒充历史统计。</summary>
public sealed record ResponseUsageValue(
    CapabilityKind Kind,
    SourceIdentity Source,
    CredentialScope CredentialScope,
    Coverage Coverage,
    DateTimeOffset FetchedAt,
    double Confidence,
    bool IsPrivate,
    bool IsEstimated,
    long Tokens,
    decimal? Cost,
    string? Currency,
    string? Model,
    string? RequestId,
    DateTimeOffset? ExpiresAt = null)
    : CapabilityValue(Kind, Source, CredentialScope, Coverage, FetchedAt, Confidence, IsPrivate, IsEstimated, ExpiresAt);

/// <summary>Probe 诊断：连接、鉴权、模型目录证据；不是用量能力，不产生用量结论。</summary>
public sealed record ProbeDiagnosticValue(
    CapabilityKind Kind,
    SourceIdentity Source,
    CredentialScope CredentialScope,
    Coverage Coverage,
    DateTimeOffset FetchedAt,
    double Confidence,
    bool IsPrivate,
    bool IsEstimated,
    bool Connected,
    bool Authenticated,
    IReadOnlyList<string> Models,
    string? Diagnostic,
    DateTimeOffset? ExpiresAt = null)
    : CapabilityValue(Kind, Source, CredentialScope, Coverage, FetchedAt, Confidence, IsPrivate, IsEstimated, ExpiresAt);

/// <summary>窗口告警阈值集合（WarnPercent/CriticalPercent 全局或能力级）。</summary>
public sealed record WarnThresholdSet(int WarnPercent, int CriticalPercent);
