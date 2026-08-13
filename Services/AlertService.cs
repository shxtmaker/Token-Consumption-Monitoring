using TokenUsageMonitorV3.Models;

namespace TokenUsageMonitorV3.Services;

/// <summary>告警：opencode 窗口阈值 + deepseek 金额阈值；临界 Toast 发作期只弹一次。</summary>
public sealed class AlertService
{
    private readonly AppSettings _settings;
    private readonly Action<string> _toast;
    private readonly HashSet<string> _criticalNotified = new();
    private readonly object _lock = new();
    public AlertService(AppSettings settings, Action<string> toast)
    {
        _settings = settings;
        _toast = toast;
    }

    public AlertLevel LastWindowLevel { get; private set; } = AlertLevel.None;
    /// <summary>评估三个窗口并返回最高级别；临界状态变更时弹 Toast。</summary>
    public AlertLevel EvaluateWindows(MonitorState state)
    {
        AlertLevel overall = AlertLevel.None;
        lock (_lock)
        {
            foreach (var w in new[] { state.Rolling, state.Weekly, state.Monthly })
            {
                var level = w.HasData
                    ? (w.Percent >= _settings.CriticalPercent || w.Status == "rate-limited" ? AlertLevel.Critical
                       : w.Percent >= _settings.WarnPercent ? AlertLevel.Warn : AlertLevel.None)
                    : AlertLevel.None;

                w.Update(w.Percent, w.Status, w.ResetsAt, level, w.LimitMicroCents, w.RemainingMicroCents);

                if (level == AlertLevel.Critical)
                {
                    if (_criticalNotified.Add(w.Name))
                        _toast($"{w.Name} 即将用尽 — 已使用 {w.Percent}%");
                }
                else
                {
                    _criticalNotified.Remove(w.Name);
                }

                if (level > overall) overall = level;
            }
        }
        LastWindowLevel = overall;
        return overall;
    }

    // ---- 页面级告警（v4：金额/token 阈值在页面配置中，选填） ----

    private readonly Dictionary<string, bool> _pageCriticalNotified = new();

    /// <summary>
    /// 按页面配置评估告警（金额与/或 token，任一维度配置即生效；留空不告警）。
    /// 返回该页最高级别；临界触发一次 Toast。
    /// </summary>
    public AlertLevel EvaluatePage(Page page, decimal amount, long tokens)
    {
        lock (_lock)
        {
            AlertLevel level = AlertLevel.None;

            if (page.AmountCriticalCny is { } ac && amount >= ac)
                level = AlertLevel.Critical;
            else if (page.AmountWarnCny is { } aw && amount >= aw && level < AlertLevel.Warn)
                level = AlertLevel.Warn;

            if (page.TokenCritical is { } tc && tokens >= tc)
                level = AlertLevel.Critical;
            else if (page.TokenWarn is { } tw && tokens >= tw && level < AlertLevel.Warn)
                level = AlertLevel.Warn;

            if (level == AlertLevel.Critical && !_pageCriticalNotified.GetValueOrDefault(page.Id))
            {
                _pageCriticalNotified[page.Id] = true;
                var detail = page.AmountCriticalCny is { } a2 && amount >= a2
                    ? $"金额 ¥{amount:F2}（阈值 ¥{a2:0.#}）"
                    : $"token {tokens:N0}（阈值 {page.TokenCritical:N0}）";
                _toast($"「{page.Name}」已达临界：{detail}");
            }
            if (level != AlertLevel.Critical)
                _pageCriticalNotified[page.Id] = false;

            return level;
        }
    }
}
