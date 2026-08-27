using System.ComponentModel;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI.Diagnostics;

namespace TokenConsumptionMonitoring.Models;

public enum ConnectionStatus { Unknown, Ok, Warn, Critical, AuthError, Offline }

public enum AlertLevel { None, Warn, Critical }

/// <summary>模型统计行（名称 + tokens + 可选金额）。</summary>
public sealed class ModelStatRow : INotifyPropertyChanged
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TokensLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CostLabel)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 全局监控状态（WPF 可绑定状态与通知）：只消费能力快照/诊断投影，
/// 不负责方法选择、URL 解析或来源合并。
/// 能力存在且有数据时显示对应区块；缺失能力默认隐藏；状态异常独立表达。
/// </summary>
public sealed class MonitorState : INotifyPropertyChanged
{
    public ConnectionStatus Connection { get; private set; } = ConnectionStatus.Unknown;
    public string ConnectionLabel { get; private set; } = "未连接";
    public string LastError { get; private set; } = "";
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public string LastSuccessLabel { get; private set; } = "暂无数据";

    public bool HasPages { get; private set; }
    public string PageName { get; private set; } = "";

    /// <summary>浮窗头部名称（= 活动页面名称）。</summary>
    public string AccountName => PageName;

    /// <summary>能力快照投影（MainPanel 与浮窗共同消费）。</summary>
    public CapabilitySnapshotViewModel Snapshot { get; } = new();

    /// <summary>扫描诊断工作台（MainPanel 中栏/左栏/右栏）。</summary>
    public ScanDiagnosticsViewModel Diagnostics { get; } = new();

    public void SetPageState(bool hasPages, string pageName)
    {
        HasPages = hasPages;
        PageName = pageName;
        Notify(nameof(HasPages), nameof(PageName), nameof(AccountName));
    }

    public void SetConnection(ConnectionStatus status, string label, string error = "")
    {
        Connection = status;
        ConnectionLabel = label;
        LastError = error;
        Notify(nameof(Connection), nameof(ConnectionLabel), nameof(LastError));
    }

    public void SetLastSuccess(DateTimeOffset? at)
    {
        LastSuccessAt = at;
        LastSuccessLabel = at is null ? "暂无数据" : $"最后更新 {at.Value.ToLocalTime():HH:mm:ss}";
        Notify(nameof(LastSuccessLabel));
    }

    /// <summary>应用一次运行时结果：更新能力快照（浮窗/MainPanel 右栏）。</summary>
    public void ApplySnapshot(CapabilitySnapshot snapshot, bool showDailyUsage)
    {
        Snapshot.Update(snapshot, showDailyUsage);
        SetLastSuccess(snapshot.Metadata.FetchedAt);
        SetConnection(
            snapshot.Status is SnapshotStatus.Success or SnapshotStatus.ProbeOnly
                ? ConnectionStatus.Ok
                : snapshot.Status switch
                {
                    SnapshotStatus.SuccessPartial => ConnectionStatus.Warn,
                    SnapshotStatus.AuthRequired => ConnectionStatus.AuthError,
                    SnapshotStatus.Forbidden => ConnectionStatus.AuthError,
                    SnapshotStatus.Stale => ConnectionStatus.Warn,
                    SnapshotStatus.NoData => ConnectionStatus.Unknown,
                    _ => ConnectionStatus.Offline,
                },
            snapshot.Status switch
            {
                SnapshotStatus.Success => "正常",
                SnapshotStatus.SuccessPartial => "部分可用",
                SnapshotStatus.ProbeOnly => "连接正常",
                SnapshotStatus.NoData => "暂无数据",
                SnapshotStatus.AuthRequired => "需要鉴权",
                SnapshotStatus.Forbidden => "权限不足",
                SnapshotStatus.Stale => "数据已过期",
                SnapshotStatus.RateLimited => "请求受限",
                SnapshotStatus.SchemaMismatch => "响应结构不匹配",
                SnapshotStatus.TemporaryFailure => "暂时失败",
                SnapshotStatus.PermanentFailure => "无用量来源",
                _ => "未连接",
            },
            snapshot.Status is SnapshotStatus.AuthRequired or SnapshotStatus.Forbidden
                ? "点击按钮可重新登录/更新凭据"
                : "");
    }

    /// <summary>应用扫描诊断（MainPanel 左栏/中栏/右栏）。</summary>
    public void ApplyDiagnostics(PageConfigRecord page, ScanReport? report, string? effectiveMethodId)
        => Diagnostics.Update(page, report, effectiveMethodId);

    public void SetScanning(bool scanning, PageConfigRecord? page = null)
        => Diagnostics.SetScanning(scanning, page);

    public void UpdateCountdowns()
    {
        foreach (var w in Snapshot.Windows) w.UpdateCountdown();
    }

    public void ClearRuntime()
    {
        Snapshot.Reset();
        Diagnostics.Clear();
        SetConnection(ConnectionStatus.Unknown, "未连接", "");
        SetLastSuccess(null);
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
