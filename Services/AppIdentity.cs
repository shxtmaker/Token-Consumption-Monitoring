namespace TokenConsumptionMonitoring.Services;

/// <summary>TokenConsumptionMonitoring 的正式产品标识集合。</summary>
/// <remarks>
/// 所有持久化标识都从这里读取。此模块不探测、不迁移其他产品名称或旧数据目录。
/// </remarks>
public static class AppIdentity
{
    public const string ProductName = "TokenConsumptionMonitoring";
    public const string AssemblyName = ProductName;
    public const string ExecutableName = ProductName + ".exe";
    public const string DataDirectoryName = ProductName;
    public const string ApiKeyPrefix = ProductName + ".ApiKey";
    public const string OAuthTarget = ProductName + ".OAuthTokens";
    public const string DeepSeekCookiesTarget = ProductName + ".DeepSeekCookies";
    public const string MutexName = ProductName + "_SingleInstance";
    public const string AutoStartValueName = ProductName;

    public static string ApiKeyTarget(string pageId) => $"{ApiKeyPrefix}.{pageId}";
}
