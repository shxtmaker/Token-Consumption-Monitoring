namespace TokenConsumptionMonitoring.Models.Usage;

/// <summary>快照元数据：绑定页面身份、配置指纹、获取时间与当前方法。</summary>
public sealed record SnapshotMetadata(
    string PageId,
    string ConfigurationFingerprint,
    DateTimeOffset FetchedAt,
    string? SelectedMethodId,
    RefreshReason Reason);

/// <summary>
/// 能力化用量快照：某一查询时刻得到的、带数据来源和时间信息的用量观察结果。
/// 只表达实际可获得的数据；模型目录与估算成本不进入默认展示列表。
/// </summary>
public sealed record CapabilitySnapshot
{
    /// <summary>无数据/空快照（兼容未扫描页面）。</summary>
    public static CapabilitySnapshot Empty(string pageId, string fingerprint) => new()
    {
        Metadata = new SnapshotMetadata(pageId, fingerprint, DateTimeOffset.UtcNow, null, RefreshReason.Poll),
        Status = SnapshotStatus.NoData,
        Capabilities = Array.Empty<CapabilityValue>(),
    };

    public required SnapshotMetadata Metadata { get; init; }
    public required SnapshotStatus Status { get; init; }
    public required IReadOnlyList<CapabilityValue> Capabilities { get; init; }

    public IEnumerable<RollingWindowValue> Windows =>
        Capabilities.OfType<RollingWindowValue>();
    public IEnumerable<BalanceQuotaValue> Balances =>
        Capabilities.OfType<BalanceQuotaValue>();
    public IEnumerable<ReportedUsageValue> ReportedUsages =>
        Capabilities.OfType<ReportedUsageValue>();
    public IEnumerable<ReportedCostValue> ReportedCosts =>
        Capabilities.OfType<ReportedCostValue>();
    public IEnumerable<EstimatedCostValue> EstimatedCosts =>
        Capabilities.OfType<EstimatedCostValue>();
    public IEnumerable<ProbeDiagnosticValue> ProbeDiagnostics =>
        Capabilities.OfType<ProbeDiagnosticValue>();
    public IEnumerable<ResponseUsageValue> ResponseUsages =>
        Capabilities.OfType<ResponseUsageValue>();

    public IEnumerable<T> Of<T>() where T : CapabilityValue => Capabilities.OfType<T>();

    public T? FirstOf<T>() where T : CapabilityValue => Of<T>().FirstOrDefault();

    public bool Has(CapabilityKind kind) => Capabilities.Any(c => c.Kind == kind);
}