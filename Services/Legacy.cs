namespace TokenConsumptionMonitoring.Services;

/// <summary>
/// 兼容标识：为读取既有用户数据而保留的旧名称、路径、凭据 target 与存储键。
/// 这些字符串是持久化的用户数据标识，不能因代码重命名而失效；新代码仅通过本类引用。
/// </summary>
public static class Legacy
{
    /// <summary>旧凭据 target 前缀（Windows 凭据管理器，页面 API key）。</summary>
    public const string ApiKeyPrefix = "TokenConsumptionMonitoring.ApiKey";

    /// <summary>旧全局 OAuth token 凭据 target（opencode OAuthTokens）。</summary>
    public const string OAuthTarget = "TokenConsumptionMonitoring.OAuthTokens";

    /// <summary>旧 DeepSeek 控制台 cookie 凭据 target。</summary>
    public const string DeepSeekCookiesTarget = "TokenConsumptionMonitoring.DeepSeekCookies";

    /// <summary>旧单实例互斥量名。</summary>
    public const string MutexName = "TokenConsumptionMonitoring_SingleInstance";

    /// <summary>旧自动启动注册表值名。</summary>
    public const string AutoStartValueName = "TokenConsumptionMonitoring";

    /// <summary>旧发布 exe 文件名（覆盖升级入口）。</summary>
    public const string ExeName = "TokenConsumptionMonitoring.exe";

    /// <summary>旧数据目录名（%APPDATA%\TokenConsumptionMonitoring，先读后迁移）。</summary>
    public const string DataDirectoryName = "TokenConsumptionMonitoring";

    /// <summary>旧日志目录名（与旧数据目录相同）。</summary>
    public const string LogDirectoryName = "TokenConsumptionMonitoring";

    /// <summary>旧临时目录名（ZCode 只读副本缓存）。</summary>
    public const string TempDirectoryName = "TokenConsumptionMonitoring";

    /// <summary>当前项目数据目录名（迁移成功后写入路径；从旧目录先读后迁移）。</summary>
    public const string CurrentDataDirectoryName = "TokenConsumptionMonitoring";

    /// <summary>由旧 ApiKey 前缀构造页面凭据 target。</summary>
    public static string ApiKeyTarget(string pageId) => $"{ApiKeyPrefix}.{pageId}";
}
