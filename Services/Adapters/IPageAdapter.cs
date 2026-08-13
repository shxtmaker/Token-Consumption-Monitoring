using TokenUsageMonitorV3.Models;

namespace TokenUsageMonitorV3.Services.Adapters;

/// <summary>适配器拉取结果（页面数据 + 连接状态）。</summary>
public sealed class PageData
{
    public ConnectionStatus Status { get; init; } = ConnectionStatus.Unknown;
    public string StatusLabel { get; init; } = "";
    public string Error { get; init; } = "";

    // 窗口限额（WindowLimit 适配器）
    public (int Percent, string Status, DateTimeOffset? ResetsAt)? Rolling { get; init; }
    public (int Percent, string Status, DateTimeOffset? ResetsAt)? Weekly { get; init; }
    public (int Percent, string Status, DateTimeOffset? ResetsAt)? Monthly { get; init; }
    public (long? LimitMicroCents, long? RemainingMicroCents)? RollingAbsolute { get; init; }
    public (long? LimitMicroCents, long? RemainingMicroCents)? WeeklyAbsolute { get; init; }
    public (long? LimitMicroCents, long? RemainingMicroCents)? MonthlyAbsolute { get; init; }

    // 用量（ConsoleSession 适配器：token + 金额）
    public long TotalTokens { get; init; }
    public decimal TotalCost { get; init; }
    public string CostCurrency { get; init; } = "";
    public List<(string Model, long Tokens, decimal Cost)> ModelRows { get; init; } = new();

    // 通用
    public List<string> Models { get; init; } = new();

    // 官方余额（DeepSeekApi 适配器）
    public decimal? BalanceCny { get; init; }
    public string BalanceCurrency { get; init; } = "";
}

/// <summary>页面适配器接口：按页面配置拉取数据。协议决定认证方式。</summary>
public interface IPageAdapter
{
    AdapterKind Kind { get; }

    /// <summary>拉取页面数据（key 从凭据管理器按页面读取；ConsoleSession 页面用全局会话）。</summary>
    Task<PageData> FetchAsync(Page page, CancellationToken ct);

    /// <summary>探测连接（30s 循环；与 FetchAsync 共用认证逻辑）。</summary>
    Task<(bool Ok, string Error)> ProbeAsync(Page page, CancellationToken ct);
}
