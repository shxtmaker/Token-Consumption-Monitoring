namespace TokenUsageMonitorV3.Services;

/// <summary>页面适配器类型（按 BaseURL 自动解析）。</summary>
public enum AdapterKind
{
    /// <summary>opencode 网关：窗口限额（/zen/go/v1/usage + /api/go/status）。</summary>
    WindowLimit,

    /// <summary>DeepSeek 控制台：官方会话用量（WebView2 页面捕获）。</summary>
    ConsoleSession,

    /// <summary>DeepSeek 官方 API（api.deepseek.com + API key）：连接/模型 + 官方余额。</summary>
    DeepSeekApi,

    /// <summary>通用探测：连接状态 + 模型列表（无官方用量数据源）。</summary>
    Probe,
}

/// <summary>适配器注册表：BaseURL → 适配器。协议决定认证方式。</summary>
public static class AdapterRegistry
{
    public static AdapterKind Resolve(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return AdapterKind.Probe;
        var u = baseUrl.ToLowerInvariant();
        if (u.Contains("opencode.ai")) return AdapterKind.WindowLimit;
        if (u.Contains("platform.deepseek.com")) return AdapterKind.ConsoleSession;
        if (u.Contains("api.deepseek.com")) return AdapterKind.DeepSeekApi;
        return AdapterKind.Probe;
    }

    public static string Describe(AdapterKind kind) => kind switch
    {
        AdapterKind.WindowLimit => "opencode 网关（窗口限额）",
        AdapterKind.ConsoleSession => "DeepSeek 控制台（官方会话用量）",
        AdapterKind.DeepSeekApi => "DeepSeek 官方 API（余额/模型）",
        _ => "通用探测（连接 + 模型列表）",
    };
}
