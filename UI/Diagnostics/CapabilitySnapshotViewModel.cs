using System.Collections.ObjectModel;
using System.ComponentModel;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.UI.Diagnostics;

/// <summary>
/// 能力快照视图模型：MainPanel 与浮窗共同消费的能力化投影。
/// 能力存在且有数据时显示对应区块；缺失能力默认隐藏；无数据/过期/鉴权失败由公共状态模型表达。
/// </summary>
public sealed class CapabilitySnapshotViewModel : INotifyPropertyChanged
{
    public ObservableCollection<WindowRowViewModel> Windows { get; } = new();
    public ObservableCollection<ModelStatRow> ModelRows { get; } = new();
    public ObservableCollection<ReportedCostRowViewModel> ReportedCostRows { get; } = new();
    public ObservableCollection<ModelStatRow> DeepSeekRows { get; } = new();

    public bool HasWindows => Windows.Count > 0;
    public bool HasReportedUsage => ReportedUsagePresent;
    public bool HasReportedCosts => ReportedCostRows.Count > 0;
    public bool HasBalance => !string.IsNullOrEmpty(BalanceLabel);

    /// <summary>窗口区块（窗口行 + 余额栏）整体可见性：有任一窗口或余额即显示。</summary>
    public bool HasWindowsOrBalance => HasWindows || HasBalance;
    public bool HasProbeMessage => !string.IsNullOrEmpty(StatusMessage);

    public string ReportedUsageLabel { get; private set; } = "";
    public long TotalTokens { get; private set; }
    public string TotalCostLabel { get; private set; } = "";
    public bool ReportedUsagePresent { get; private set; }
    public string BalanceLabel { get; private set; } = "";
    public string StatusMessage { get; private set; } = "";
    public bool IsStale { get; private set; }
    public bool IsAuthRequired { get; private set; }
    public bool IsProbeOnly { get; private set; }

    /// <summary>是否有任何可展示的能力（浮窗整体可见性）。</summary>
    public bool HasCapabilities => HasWindows || HasReportedUsage || HasReportedCosts || HasBalance;

    /// <summary>整体状态文字（连接正常但无用量 / 需要鉴权 / 数据过期 / 无数据）。</summary>
    public string StatusLabel { get; private set; } = "暂无数据";

    public void Update(CapabilitySnapshot snapshot, bool showDailyUsage)
    {
        // 窗口能力：动态列出（名称与数量来自快照，不预设 5h/周/月）
        var expectedWindows = snapshot.Windows.ToList();
        while (Windows.Count > expectedWindows.Count) Windows.RemoveAt(Windows.Count - 1);
        while (Windows.Count < expectedWindows.Count)
            Windows.Add(new WindowRowViewModel());
        for (var i = 0; i < expectedWindows.Count; i++)
        {
            var w = expectedWindows[i];
            Windows[i].Set(w.WindowName, w.WindowName.Contains("周") || w.WindowName.Contains("月") || w.WindowName.Contains("周期"));
            Windows[i].Update(w, AlertLevel.None);
        }

        // 报告用量：总量 + 模型行（仅非 probe 能力；「今日用量」关闭时隐藏 zcode 本地记录）
        var usage = snapshot.ReportedUsages.FirstOrDefault();
        if (!showDailyUsage && usage?.Source.Provider == "zcode") usage = null;
        ReportedUsagePresent = usage is not null;
        TotalTokens = usage?.TotalTokens ?? 0;
        ReportedUsageLabel = usage is null ? "" : FormatTokens(usage.TotalTokens);
        TotalCostLabel = usage is null || usage.Models.All(m => m.Cost is null)
            ? "" : $"金额 ¥{usage.Models.Sum(m => m.Cost ?? 0):F2}";

        ModelRows.Clear();
        if (usage is { Models.Count: > 0 } u)
        {
            foreach (var m in u.Models.OrderByDescending(x => x.Tokens))
            {
                var row = new ModelStatRow(m.Model, withCost: m.Cost is not null && u.Models.Any(x => x.Cost is not null));
                row.Set(m.Tokens, m.Cost ?? 0);
                ModelRows.Add(row);
            }
        }

        ReportedCostRows.Clear();
        foreach (var cost in snapshot.ReportedCosts)
            if (!cost.IsEstimated)
                ReportedCostRows.Add(new ReportedCostRowViewModel(cost.Amount, cost.Currency));

        // 余额/额度（独立区块；多来源不合并，取首选来源）
        var balance = snapshot.Balances.FirstOrDefault();
        BalanceLabel = "";
        if (balance is not null && balance.Balance is { } b)
            BalanceLabel = $"余额 {b:F2} {balance.Currency}".Trim();
        else if (balance is not null && balance.Remaining is { } r && balance.Limit is { } l)
            BalanceLabel = $"剩余 {r:0.##} / {l:0.##} {balance.Currency}".Trim();

        // 状态表达：过期/鉴权/Probe-only/暂无数据（只显示状态，不触发用量区块）
        IsStale = snapshot.IsStale;
        IsProbeOnly = snapshot.Status == SnapshotStatus.ProbeOnly;
        IsAuthRequired = snapshot.Status == SnapshotStatus.AuthRequired;
        StatusMessage = IsAuthRequired ? "需要鉴权/凭据" : IsStale ? "数据已过期（保留旧值）" : "";

        StatusLabel = snapshot.Status switch
        {
            SnapshotStatus.AuthRequired => "需要鉴权/登录",
            SnapshotStatus.Forbidden => "权限不足（403）",
            SnapshotStatus.RateLimited => "请求受限（429）",
            SnapshotStatus.SchemaMismatch => "响应结构不匹配",
            SnapshotStatus.Stale => "数据已过期（保留旧值）",
            SnapshotStatus.TemporaryFailure => "暂时获取失败",
            SnapshotStatus.PermanentFailure => "无可用用量来源",
            SnapshotStatus.ProbeOnly => "连接正常，暂无可展示用量能力",
            SnapshotStatus.NoData => "暂无数据",
            SnapshotStatus.SuccessPartial => "部分数据不可用",
            _ => "暂无数据",
        };
        if (HasCapabilities && !IsStale && !IsAuthRequired)
            StatusLabel = "正常";

        // DeepSeek 兼容行（控制台明细迁移：模型行已含在 ReportedUsage，这里保持空白以让新视图接管）
        DeepSeekRows.Clear();

        Notify(nameof(HasWindows), nameof(HasReportedUsage), nameof(HasReportedCosts), nameof(HasBalance), nameof(HasProbeMessage),
            nameof(HasCapabilities), nameof(ReportedUsageLabel), nameof(BalanceLabel), nameof(StatusLabel),
            nameof(TotalCostLabel), nameof(ReportedUsagePresent), nameof(StatusMessage), nameof(IsStale), nameof(IsAuthRequired), nameof(IsProbeOnly),
            nameof(HasWindowsOrBalance));
    }

    public void Reset()
    {
        Windows.Clear(); ModelRows.Clear(); ReportedCostRows.Clear(); DeepSeekRows.Clear();
        ReportedUsageLabel = ""; BalanceLabel = ""; StatusMessage = "";
        TotalTokens = 0; StatusLabel = "暂无数据";
        ReportedUsagePresent = false;
        IsStale = IsAuthRequired = IsProbeOnly = false;
        Notify(nameof(HasWindows), nameof(HasReportedUsage), nameof(HasReportedCosts), nameof(HasBalance), nameof(HasCapabilities),
            nameof(HasProbeMessage), nameof(ReportedUsageLabel), nameof(TotalTokens), nameof(BalanceLabel),
            nameof(StatusMessage), nameof(StatusLabel), nameof(TotalCostLabel), nameof(ReportedUsagePresent),
            nameof(IsStale), nameof(IsAuthRequired), nameof(IsProbeOnly), nameof(HasWindowsOrBalance));
    }

    public static string FormatTokens(long tokens)
        => tokens >= 1_000_000 ? $"{tokens / 1_000_000m:0.#}M"
         : tokens >= 1_000 ? $"{tokens / 1_000m:0.#}K"
         : tokens.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

/// <summary>报告成本行：只显示供应商实际返回的金额，不显示估算成本。</summary>
public sealed class ReportedCostRowViewModel
{
    public decimal Amount { get; }
    public string Currency { get; }
    public string Label => $"{Amount:0.####} {Currency}";

    public ReportedCostRowViewModel(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }
}
