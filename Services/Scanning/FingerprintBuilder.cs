using System.Security.Cryptography;
using System.Text;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>
/// 检测指纹：描述页面端点、协议、凭据范围、方法版本和本地来源状态的可比较摘要。
/// 指纹变化表示此前的查询选择可能失效，需要重新扫描。
/// 指纹不包含密钥或会话秘密。
/// </summary>
public sealed class FingerprintBuilder
{
    private readonly IReadOnlyList<string> _methodVersions;

    public FingerprintBuilder(IEnumerable<string> methodVersions)
        => _methodVersions = methodVersions.OrderBy(x => x, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToList();

    public string Build(PageConfigRecord page, bool localRecordsPresent)
    {
        var sb = new StringBuilder();
        sb.Append(Uri.TryCreate(page.BaseUrl, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path).TrimEnd('/') : page.BaseUrl);
        sb.Append("|").Append(page.Protocol);
        sb.Append("|").Append(page.CredentialRef.Kind);
        if (page.CredentialRef.Target is { } t) sb.Append(":").Append(t);
        sb.Append("|methods=").Append(string.Join(",", _methodVersions));
        sb.Append("|local=").Append(localRecordsPresent ? "1" : "0");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }
}
