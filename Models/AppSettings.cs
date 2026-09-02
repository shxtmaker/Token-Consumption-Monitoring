namespace TokenConsumptionMonitoring.Models;

/// <summary>持久化设置（%APPDATA%\TokenConsumptionMonitoring\settings.json）。</summary>
public sealed class AppSettings
{
    /// <summary>设置 schema 版本（未知高版本读取时保留原文件，不覆盖）。</summary>
    public int SchemaVersion { get; set; } = 1;

    public int PollIntervalMinutes { get; set; } = 30;        // 官方接口轮询（1–120）
    public int ProbeIntervalSeconds { get; set; } = 60;       // 会话自检/连接探测
    public int WarnPercent { get; set; } = 80;                // opencode 窗口告警
    public int CriticalPercent { get; set; } = 95;
    public bool AutoStart { get; set; } = true;

    /// <summary>是否显示桌面悬浮组件（设置窗「桌面组件」开关）。</summary>
    public bool ShowFloatingWidget { get; set; } = true;

    /// <summary>是否在桌面组件显示今日 token 消耗（zcode 本地数据；设置窗「token消耗量」开关）。</summary>
    public bool ShowDailyTokens { get; set; } = true;

    /// <summary>桌面组件锁定状态（锁定=不可拖动且置顶；解锁=可拖动、可被覆盖）。</summary>
    public bool WidgetLocked { get; set; }

    /// <summary>桌面组件背景不透明度百分比（100=纯色黑背景，0=背景全透仅剩内容；设置窗「不透明度」滑杆）。</summary>
    public int WidgetOpacityPercent { get; set; } = 100;
    /// <summary>当前激活页面 Id（v4 页面模型）。</summary>
    public string? ActivePageId { get; set; }
}
