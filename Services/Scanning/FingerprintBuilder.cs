using System.Security.Cryptography;
using System.Text;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>
/// 检测指纹：描述页面端点、协议、凭据范围、方法版本和本地来源状态的可比较摘要。
/// 指纹变化表示此前的查询选择可能失效，需要重新扫描。
/// 指纹不包含密钥或会话秘密。
/// </summary>
public sealed class FingerprintBuilder
{
    private readonly IReadOnlyList<string> _methodImplementations;

    public FingerprintBuilder(IEnumerable<QueryMethodDescriptor> descriptors)
        => _methodImplementations = descriptors
            .Select(d => $"{d.MethodId}@{d.ImplementationVersion}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>保留简短构造入口；没有方法 ID 时使用显式占位，避免把版本误当作方法身份。</summary>
    public FingerprintBuilder(IEnumerable<string> methodVersions)
        : this(methodVersions.Select(version => new QueryMethodDescriptor(
            "unknown", SourceKind.Probe, CredentialClass.None,
            QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ProbeDiagnostic),
            SourceStability.ProbeOnly, MethodEnablement.Always, int.MaxValue, version)))
    {
    }

    /// <summary>
    /// 生成页面配置指纹。
    /// 本地记录来源已整体下线：不再读取本机 zcode SQLite，指纹恒不含本机记录成分。
    /// localRecordsPresent / localSourceSignatures 仅保留以兼容旧调用方与测试；结果不再受其影响。
    /// </summary>
    public string Build(PageConfigRecord page, bool localRecordsPresent = false, IEnumerable<string>? localSourceSignatures = null)
    {
        var sb = new StringBuilder();
        sb.Append(Uri.TryCreate(page.BaseUrl, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path).TrimEnd('/') : page.BaseUrl);
        sb.Append("|").Append(page.Protocol);
        sb.Append("|credential=").Append(page.CredentialRef.ResolveClass());
        sb.Append("|reference=").Append(page.CredentialRef.Target);
        sb.Append("|session=").Append(page.SessionGeneration);
        sb.Append("|compat=").Append(string.Join(",", page.EnabledCompatibilityMethods.OrderBy(x => x, StringComparer.Ordinal)));
        sb.Append("|methods=").Append(string.Join(",", _methodImplementations));
        var localSignatures = localSourceSignatures?.OrderBy(x => x, StringComparer.Ordinal).ToList() ?? new List<string>();
        sb.Append("|local=").Append(string.Join(",", localSignatures));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }
}
