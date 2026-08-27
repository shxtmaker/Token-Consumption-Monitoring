using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services;

/// <summary>
/// 能力告警：按能力与来源身份评估窗口/额度告警。
/// - 带百分比和上限的窗口使用 WarnPercent/CriticalPercent；限流状态不触发新告警。
/// - 余额/额度仅当存在明确上限、剩余值和用户阈值时告警。
/// - 报告实际费用可作为未来费用告警基础；估算成本不告警。
/// - 无数据、过期、鉴权失败、Probe 和临时失败只显示状态，不触发用量告警；部分成功仅评估新鲜条目。
/// - 去重键 = PageId + SourceIdentity + CapabilityKind + WindowKey。
/// </summary>
public sealed class AlertService
{
    private readonly AppSettings _settings;
    private readonly Action<string> _toast;
    private readonly HashSet<string> _criticalNotified = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public AlertService(AppSettings settings, Action<string> toast)
    {
        _settings = settings;
        _toast = toast;
    }

    public AlertLevel LastLevel { get; private set; } = AlertLevel.None;

    /// <summary>快照告警评估结果：整体级别 + 每个窗口的级别（UI 进度条颜色用）。</summary>
    public sealed record SnapshotAlertResult(AlertLevel Overall, IReadOnlyList<(string WindowKey, AlertLevel Level)> Windows);

    /// <summary>评估快照中的窗口与余额能力，返回整体级别与窗口级别；新进入临界状态时弹一次 Toast。</summary>
    public SnapshotAlertResult EvaluateSnapshot(CapabilitySnapshot snapshot, string pageId)
    {
        AlertLevel overall = AlertLevel.None;
        var windowLevels = new List<(string, AlertLevel)>(snapshot.Windows.Count());
        lock (_lock)
        {
            // 失败、过期、无数据和仅探测快照只显示状态，不产生新的用量告警。
            var canAlert = snapshot.Status is SnapshotStatus.Success or SnapshotStatus.SuccessPartial;
            foreach (var window in snapshot.Windows)
            {
                var l = canAlert ? EvaluateWindow(pageId, window) : AlertLevel.None;
                if (!canAlert) _criticalNotified.Remove(DedupKey(pageId, window));
                windowLevels.Add((window.WindowKey, l));
                overall = Higher(overall, l);
            }

            if (canAlert)
                foreach (var balance in snapshot.Balances)
                    overall = Higher(overall, EvaluateBalance(pageId, balance));
            else
                foreach (var balance in snapshot.Balances)
                    _criticalNotified.Remove(DedupKey(pageId, balance));
        }
        LastLevel = overall;
        return new SnapshotAlertResult(overall, windowLevels);
    }

    private AlertLevel EvaluateWindow(string pageId, RollingWindowValue window)
    {
        var key = DedupKey(pageId, window);
        // 过期/估算/无百分比上限的数据不触发用量告警
        if (window.IsStale || window.IsEstimated || window.IsExpired(DateTimeOffset.UtcNow))
        {
            _criticalNotified.Remove(key);
            return AlertLevel.None;
        }
        var normalizedStatus = window.Status?.Replace('_', '-');
        if (normalizedStatus?.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) == true
            || window.Percent is null && window.Limit is null)
        {
            _criticalNotified.Remove(key);
            return AlertLevel.None;
        }

        var percent = window.EffectivePercent();
        var level = percent >= _settings.CriticalPercent
            ? AlertLevel.Critical
            : percent >= _settings.WarnPercent
                ? AlertLevel.Warn
                : AlertLevel.None;

        NotifyTransition(key, level, $"{window.WindowName} 即将用尽 — 已使用 {percent}%");
        return level;
    }

    private AlertLevel EvaluateBalance(string pageId, BalanceQuotaValue balance)
    {
        var key = DedupKey(pageId, balance);
        // 余额/额度只有存在明确上限、剩余值和用户阈值时告警
        if (balance.IsStale || balance.IsEstimated || balance.IsExpired(DateTimeOffset.UtcNow)
            || balance.Limit is not { } limit || limit <= 0 || balance.Remaining is not { } rem)
        {
            _criticalNotified.Remove(key);
            return AlertLevel.None;
        }

        var pct = (int)Math.Round(Math.Max(0, Math.Min(100, (double)(limit - rem) / (double)limit * 100)));
        var level = pct >= _settings.CriticalPercent
            ? AlertLevel.Critical
            : pct >= _settings.WarnPercent
                ? AlertLevel.Warn
                : AlertLevel.None;

        NotifyTransition(key, level, $"额度 已使用 {pct}%");
        return level;
    }

    /// <summary>去重/转换：仅在新进入 Critical 时弹一次 Toast；离开 Critical 后允许再次提示。</summary>
    private void NotifyTransition(string key, AlertLevel level, string message)
    {
        if (level == AlertLevel.Critical)
        {
            if (_criticalNotified.Add(key))
                _toast(message);
        }
        else
        {
            _criticalNotified.Remove(key);
        }
    }

    private static string DedupKey(string pageId, CapabilityValue value)
        => $"{pageId}|{value.Source.StableKey}|{value.Kind}|{KeyOf(value)}";

    private static string KeyOf(CapabilityValue value) => CapabilityItemKey.For(value).ItemKey;

    private static AlertLevel Higher(AlertLevel a, AlertLevel b)
        => b > a ? b : a;
}
