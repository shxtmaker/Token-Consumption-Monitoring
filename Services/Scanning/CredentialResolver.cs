using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Scanning;

/// <summary>
/// 凭据解析：只返回与方法声明匹配的凭据引用和脱敏范围，不读取全部凭据。
/// 秘密原文只在方法内部通过 ReadApiKey 读取，不落入日志/缓存/诊断。
/// </summary>
public sealed class CredentialResolver
{
    private readonly PageConfigRecord _page;

    public CredentialResolver(PageConfigRecord page) => _page = page;

    /// <summary>页面声明的凭据引用。</summary>
    public CredentialReference Reference => _page.CredentialRef;

    /// <summary>按页面声明推断的凭据类别。</summary>
    public CredentialClass DeclaredClass => Reference.ResolveClass();

    /// <summary>是否已配置页面 API key（凭据存在且非空；不输出原文）。</summary>
    public bool HasApiKey => !string.IsNullOrEmpty(ReadApiKey());

    /// <summary>读取页面 API key 原文（仅方法内部使用，禁止日志/缓存）。</summary>
    public string? ReadApiKey()
    {
        if (DeclaredClass != CredentialClass.ApiKey) return null;
        return CredentialStore.TryReadSecret(Reference.Target!, out var key) && !string.IsNullOrWhiteSpace(key)
            ? key : null;
    }

    /// <summary>读取命名 target 的 API key（非页面凭据引用场景）。</summary>
    public static string? ReadApiKey(string target) =>
        CredentialStore.TryReadSecret(target, out var key) && !string.IsNullOrWhiteSpace(key) ? key : null;

    /// <summary>该方法是否匹配页面声明的能力范围（凭据类别一致 + 端点提示匹配）。</summary>
    public bool Matches(CredentialClass requiredClass, string? providerHint = null)
    {
        if (requiredClass is CredentialClass.LocalRecord or CredentialClass.None) return true;
        if (requiredClass == CredentialClass.ApiKey) return HasApiKey;
        return DeclaredClass == requiredClass;
    }

    /// <summary>脱敏凭据范围。</summary>
    public CredentialScope Scope => new(DeclaredClass, Provider: ProviderOf(_page.BaseUrl));

    /// <summary>从 Base URL 提取低置信度供应商提示（仅用于候选证据，不决定方法）。</summary>
    public static string? ProviderOf(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Host : baseUrl;
        foreach (var (key, provider) in new[]
        {
            ("opencode.ai", "opencode"),
            ("commandcode.ai", "commandcode"),
            ("deepseek.com", "deepseek"),
            ("openrouter.ai", "openrouter"),
            ("openai.com", "openai"),
            ("anthropic.com", "anthropic"),
            ("x.ai", "xai"),
            ("fireworks.ai", "fireworks"),
        })
        {
            if (host.Contains(key, StringComparison.OrdinalIgnoreCase)) return provider;
        }
        return null;
    }
}
