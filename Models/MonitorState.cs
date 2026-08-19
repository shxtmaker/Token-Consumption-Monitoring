using TokenUsageMonitorV3.Services;

namespace TokenUsageMonitorV3.Models;

public enum ConnectionStatus { Unknown, Ok, Warn, Critical, AuthError, Offline }

public enum AlertLevel { None, Warn, Critical }

/// <summary>模型统计行（名称 + tokens + 可选金额）。</summary>
public sealed class ModelStatRow : System.ComponentModel.INotifyPropertyChanged
{
    private readonly bool _withCost;

    public string Name { get; }
    public long Tokens { get; private set; }
    public decimal CostCny { get; private set; }
    public string TokensLabel { get; private set; } = "--";
    public string CostLabel { get; private set; } = "";

    public ModelStatRow(string name, bool withCost = false)
    {
        Name = name;
        _withCost = withCost;
    }

    public void Set(long tokens, decimal costCny = 0)
    {
        Tokens = tokens;
        CostCny = costCny;
        TokensLabel = MonitorState.FormatTokens(tokens);
        CostLabel = _withCost ? $"¥{costCny:F2}" : "";
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TokensLabel)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CostLabel)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>单窗口（5h滚动 / 周 / 月）状态。</summary>
public sealed class WindowState : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; }
    private readonly bool _dayFormat;   // 周/月窗口倒计时按“X天Y小时”显示
    public int Percent { get; private set; } = -1;
    public string Status { get; private set; } = "";
    public DateTimeOffset? ResetsAt { get; private set; }
    public AlertLevel Level { get; private set; } = AlertLevel.None;
    public long? LimitMicroCents { get; private set; }
    public long? RemainingMicroCents { get; private set; }

    public bool HasData => Percent >= 0;
    public string PercentLabel => Percent < 0 ? "--" : $"{Percent}%";
    public string RemainingLabel => Percent < 0 ? "--" : $"{Math.Max(0, 100 - Percent)}%";

    // 倒计时拆分槽位（值定宽右对齐 + 单位定宽）：保证多行左右边缘严格对齐
    public string CountdownV1 { get; private set; } = "--";
    public string CountdownU1 { get; private set; } = "";
    public string CountdownV2 { get; private set; } = "";
    public string CountdownU2 { get; private set; } = "";
    public string AbsoluteLabel =>
        (LimitMicroCents is null || RemainingMicroCents is null)
            ? "" : $"限额 {FormatUsd(LimitMicroCents.Value)} · 剩余 {FormatUsd(RemainingMicroCents.Value)}";

    public WindowState(string name, bool dayFormat = false)
    {
        Name = name;
        _dayFormat = dayFormat;
    }

    public void Update(int percent, string status, DateTimeOffset? resetsAt, AlertLevel level,
        long? limit = null, long? remaining = null)
    {
        Percent = percent;
        Status = status;
        ResetsAt = resetsAt;
        Level = level;
        LimitMicroCents = limit;
        RemainingMicroCents = remaining;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PercentLabel)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RemainingLabel)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AbsoluteLabel)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Level)));
        UpdateCountdown();
    }

    public void UpdateCountdown()
    {
        var span = ResetsAt is null ? (TimeSpan?)null : ResetsAt.Value - DateTimeOffset.UtcNow;
        if (span is null) SetCountdown("--", "", "", "");
        else if (span <= TimeSpan.Zero) SetCountdown("即将重置", "", "", "");
        else if (_dayFormat && span.Value.TotalHours >= 24)
            SetCountdown($"{(int)span.Value.TotalDays:00}", "d", $"{span.Value.Hours:00}", "h");
        else
            SetCountdown($"{(int)span.Value.TotalHours:00}", "h", $"{span.Value.Minutes:00}", "m");
    }

    private void SetCountdown(string v1, string u1, string v2, string u2)
    {
        CountdownV1 = v1; CountdownU1 = u1; CountdownV2 = v2; CountdownU2 = u2;
        foreach (var n in new[] { nameof(CountdownV1), nameof(CountdownU1), nameof(CountdownV2), nameof(CountdownU2) })
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public static string FormatUsd(long microCents) => "$" + Money.ToUsd(microCents).ToString("N2", System.Globalization.CultureInfo.InvariantCulture);   // InvariantCulture 的 C2 会输出 ¤，改用显式美元符号

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>全局监控状态。</summary>
public sealed class MonitorState : System.ComponentModel.INotifyPropertyChanged
{
    public ConnectionStatus Connection { get; private set; } = ConnectionStatus.Unknown;
    public string ConnectionLabel { get; private set; } = "未连接";
    public string LastError { get; private set; } = "";
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public string LastSuccessLabel { get; private set; } = "暂无数据";
    public string DataSourceLabel { get; private set; } = "官方接口";

    public WindowState Rolling { get; } = new("5h滚动");
    public WindowState Weekly { get; } = new("周用量", dayFormat: true);
    public WindowState Monthly { get; } = new("月用量", dayFormat: true);

    // 账户（v4 迁移中：页面模型）
    public string AccountName { get; private set; } = "";
    public bool IsWindowLayoutPage { get; private set; }   // 三窗口布局页（opencode / Command Code：5h/周/月）
    public bool IsDeepSeekAccount { get; private set; }

    // 页面模型（v4）
    public bool HasPages { get; private set; }
    public string PageName { get; private set; } = "";
    public bool IsProbePage { get; private set; }
    public string ProbeModelsLabel { get; private set; } = "";

    public void SetPageState(bool hasPages, string pageName)
    {
        HasPages = hasPages;
        PageName = pageName;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasPages)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PageName)));
    }

    /// <summary>设置当前页面（名称 + 适配器类型 → 显示布局）。</summary>
    public void SetCurrentPage(string name, AdapterKind kind)
    {
        AccountName = name;
        PageName = name;
        IsWindowLayoutPage = kind == AdapterKind.WindowLimit || kind == AdapterKind.CommandCode;
        IsDeepSeekAccount = kind == AdapterKind.ConsoleSession || kind == AdapterKind.DeepSeekApi;   // 官方 API 整合页同显官方用量
        IsProbePage = kind == AdapterKind.Probe || kind == AdapterKind.DeepSeekApi;   // 官方 API 页同用探测视图（模型+余额）
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AccountName)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PageName)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsWindowLayoutPage)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDeepSeekAccount)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsProbePage)));
    }

    public void SetProbeModels(IEnumerable<string> models)
    {
        ProbeModelsLabel = models.Any()
            ? $"模型：{string.Join(" · ", models.Take(6))}{(models.Count() > 6 ? " …" : "")}"
            : "模型列表为空";
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProbeModelsLabel)));
    }

    // 官方余额（DeepSeek 官方 API 页面；无余额数据时隐藏）
    public string BalanceLabel { get; private set; } = "";

    public void SetBalance(decimal? balance, string currency)
    {
        BalanceLabel = balance is null ? "" : $"官方余额 ¥{balance:F2} {currency}".TrimEnd();
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BalanceLabel)));
    }

    // DeepSeek 官方用量（今日）
    public ModelStatRow DeepSeekFlash { get; } = new("deepseek-v4-flash", withCost: true);
    public ModelStatRow DeepSeekPro { get; } = new("deepseek-v4-pro", withCost: true);
    public long DeepSeekTotalTokens { get; private set; }
    public decimal DeepSeekTotalCostCny { get; private set; }
    public string DeepSeekTotalLabel { get; private set; } = "今日 -- tokens · 金额 ¥--";
    public AlertLevel DeepSeekLevel { get; private set; } = AlertLevel.None;

    public void SetDeepSeekUsage(long totalTokens, decimal costCny, long flashTokens, decimal flashCost, long proTokens, decimal proCost)
    {
        DeepSeekTotalTokens = totalTokens;
        DeepSeekTotalCostCny = costCny;
        DeepSeekTotalLabel = $"今日 {FormatTokens(totalTokens)} · 金额 ¥{costCny:F2}";
        DeepSeekFlash.Set(flashTokens, flashCost);
        DeepSeekPro.Set(proTokens, proCost);
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DeepSeekTotalLabel)));
    }

    public void SetDeepSeekLevel(AlertLevel level)
    {
        DeepSeekLevel = level;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DeepSeekLevel)));
    }

    public void SetConnection(ConnectionStatus status, string label, string error = "")
    {
        Connection = status;
        ConnectionLabel = label;
        LastError = error;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Connection)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ConnectionLabel)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LastError)));
    }

    public void SetLastSuccess(DateTimeOffset? at)
    {
        LastSuccessAt = at;
        LastSuccessLabel = at is null ? "暂无数据" : $"最后更新 {at.Value.ToLocalTime():HH:mm:ss}";
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LastSuccessLabel)));
    }

    public void SetDataSource(string label)
    {
        DataSourceLabel = label;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DataSourceLabel)));
    }

    // zcode 今日 token 消耗（按页面 API key 归属；显示开关由设置控制，无数据时整块隐藏）
    public bool ShowDailyUsage { get; private set; } = true;
    public bool HasDailyData { get; private set; }
    public bool DailyBlockVisible => ShowDailyUsage && HasDailyData;
    public string DailyTotalLabel { get; private set; } = "今日用量 --";
    public System.Collections.ObjectModel.ObservableCollection<ModelStatRow> DailyRows { get; } = new();

    public void SetShowDailyUsage(bool show)
    {
        ShowDailyUsage = show;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DailyBlockVisible)));
    }

    /// <summary>更新今日用量：总行 + 分模型行（按 tokens 降序）；无数据时隐藏整块。</summary>
    public void SetDailyUsage(long total, IEnumerable<(string Model, long Tokens)> models)
    {
        DailyTotalLabel = total > 0 ? $"今日用量 {FormatTokens(total)} tokens" : "今日用量 --";
        DailyRows.Clear();
        foreach (var (model, tokens) in models.OrderByDescending(m => m.Tokens))
        {
            var row = new ModelStatRow(model);
            row.Set(tokens);
            DailyRows.Add(row);
        }
        HasDailyData = total > 0;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DailyTotalLabel)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DailyBlockVisible)));
    }

    public static string FormatTokens(long tokens)
        => tokens >= 1_000_000 ? $"{tokens / 1_000_000m:0.#}M"
         : tokens >= 1_000 ? $"{tokens / 1_000m:0.#}K"
         : tokens.ToString();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
