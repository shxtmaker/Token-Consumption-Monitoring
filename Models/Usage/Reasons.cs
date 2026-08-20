namespace TokenConsumptionMonitoring.Models.Usage;

/// <summary>刷新原因：决定本轮是只查已选方法还是重新扫描全部候选。</summary>
public enum RefreshReason
{
    /// <summary>普通轮询（只调用已选方法）。</summary>
    Poll,

    /// <summary>新建/保存页面后扫描。</summary>
    PageSaved,

    /// <summary>配置（端点/协议/凭据引用/扫描输入）变化。</summary>
    ConfigurationChanged,

    /// <summary>应用启动且页面没有有效方法，或检测指纹变化。</summary>
    FingerprintChanged,

    /// <summary>当前方法连续失败超过冷却条件。</summary>
    ConsecutiveFailures,

    /// <summary>用户手动刷新（重新扫描）。</summary>
    Manual,
}

/// <summary>重新扫描原因（候选链再生成的触发点）。</summary>
public enum ScanReason
{
    PageSaved,
    ConfigurationChanged,
    Startup,
    FingerprintChanged,
    ConsecutiveFailures,
    Manual,
}
