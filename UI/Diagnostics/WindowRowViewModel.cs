using System.ComponentModel;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.UI.Diagnostics;

/// <summary>窗口行：滚动/周期窗口（名称、百分比、剩余、重置倒计时、绝对额度）。</summary>
public sealed class WindowRowViewModel : INotifyPropertyChanged
{
    private bool _dayFormat;

    public string Name { get; private set; } = "";
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
    public string AbsoluteLabel => (LimitMicroCents is null || RemainingMicroCents is null)
        ? "" : $"限额 {FormatUsd(LimitMicroCents.Value)} · 剩余 {FormatUsd(RemainingMicroCents.Value)}";

    /// <summary>紧凑倒计时（浮窗用）：如 19h 42m / 2d 05h / 即将重置。</summary>
    public string Countdown => ResetsAt is null
        ? "--"
        : ResetsAt.Value - DateTimeOffset.UtcNow is { } span && span > TimeSpan.Zero
            ? (span.TotalHours >= 24 ? $"{(int)span.TotalDays}d {(span.Hours):00}h" : $"{(int)span.TotalHours}h {span.Minutes:00}m")
            : "即将重置";

    public WindowRowViewModel(bool dayFormat = false) => _dayFormat = dayFormat;

    internal void Set(string name, bool dayFormat)
    {
        Name = name;
        _dayFormat = dayFormat;
        Notify(nameof(Name));
    }

    /// <summary>仅更新告警级别（不覆盖百分比；用于告警评估后的刷新）。</summary>
    public void UpdateLevel(AlertLevel level)
    {
        Level = level;
        Notify(nameof(Level));
    }

    public void Update(RollingWindowValue w, AlertLevel level)
    {
        Percent = w.EffectivePercent();
        Status = w.Status ?? "";
        ResetsAt = w.ResetsAt;
        Level = level;
        LimitMicroCents = w.Limit;
        RemainingMicroCents = w.Remaining;
        Notify(nameof(Percent), nameof(PercentLabel), nameof(RemainingLabel), nameof(Level), nameof(AbsoluteLabel));
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
        Notify(nameof(CountdownV1), nameof(CountdownU1), nameof(CountdownV2), nameof(CountdownU2), nameof(Countdown));
    }

    public static string FormatUsd(long microCents)
        => (microCents / 10_000_000m).ToString("C2", System.Globalization.CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
