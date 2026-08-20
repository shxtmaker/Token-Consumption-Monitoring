using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>
/// 扫描上下文：方法只读探测所需的页面、配置指纹与凭据解析器。
/// 凭据只返回与方法声明匹配的引用和脱敏范围；秘密原文仅在方法内部读取。
/// </summary>
public sealed class ScanContext
{
    public required PageConfigRecord Page { get; init; }
    public required string ConfigurationFingerprint { get; init; }
    public required CredentialResolver Credentials { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
