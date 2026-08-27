namespace TokenConsumptionMonitoring.Services;

/// <summary>
/// API 协议与 key 格式：支持三大 API 协议。
/// - ChatCompletions：OpenAI 兼容（OpenAI / DeepSeek / opencode 网关 / 智谱等），Bearer + sk-…
/// - Responses：OpenAI Responses API，Bearer + sk-…
/// - Anthropic：Anthropic Messages API，x-api-key + anthropic-version，sk-ant-…
/// </summary>
public static class KeyFormat
{
    public enum Protocol
    {
        ChatCompletions,
        Responses,
        Anthropic,
        DeepSeekConsole,
    }

    public static string Describe(Protocol p) => p switch
    {
        Protocol.ChatCompletions => "Chat Completions API（OpenAI 兼容：OpenAI/DeepSeek/opencode 等）",
        Protocol.Responses => "Responses API（OpenAI 新协议）",
        Protocol.Anthropic => "Anthropic API（Messages，x-api-key 认证）",
        Protocol.DeepSeekConsole => "DeepSeek 控制台（WebView2 会话登录，无 API key）",
        _ => "",
    };

    public static string KeyHint(Protocol p) => p switch
    {
        Protocol.ChatCompletions => "格式：sk-…（Bearer 认证）",
        Protocol.Responses => "格式：sk-…（Bearer 认证）",
        Protocol.Anthropic => "格式：sk-ant-…（x-api-key + anthropic-version 头）",
        Protocol.DeepSeekConsole => "无需 API key——保存后使用「登录 DeepSeek 控制台」",
        _ => "",
    };

    /// <summary>凭据管理器 target（按协议分存）。</summary>
    public static string CredentialTarget(Protocol p) => p switch
    {
        Protocol.ChatCompletions => $"{AppIdentity.ApiKeyPrefix}.ChatCompletions",
        Protocol.Responses => $"{AppIdentity.ApiKeyPrefix}.Responses",
        Protocol.Anthropic => $"{AppIdentity.ApiKeyPrefix}.Anthropic",
        _ => AppIdentity.ApiKeyPrefix,
    };

    /// <summary>供应商目录：设置页按供应商分开管理 API key。</summary>
    public sealed record ProviderDef(string Id, string Name, Protocol Protocol)
    {
        public static readonly ProviderDef[] All =
        {
            new("OpenCodeGo", "opencode Go", Protocol.ChatCompletions),
            new("OpenAI", "OpenAI", Protocol.ChatCompletions),
            new("DeepSeek", "DeepSeek", Protocol.ChatCompletions),
            new("Anthropic", "Anthropic (Claude)", Protocol.Anthropic),
            new("Custom", "自定义", Protocol.ChatCompletions),
        };

        public string Target => $"{AppIdentity.ApiKeyPrefix}.{Id}";
        public string ProtocolLabel => Describe(Protocol);
    }

    /// <summary>校验：非空、≥8 位、格式与协议匹配。返回 (是否有效, 提示)。</summary>
    public static (bool Valid, string Hint) Validate(Protocol protocol, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return (false, "请输入 API key");
        var k = key.Trim();
        if (k.Length < 8) return (false, "key 长度过短（至少 8 位）");
        switch (protocol)
        {
            case Protocol.Anthropic:
                if (!k.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase))
                    return (false, "Anthropic API key 应以 sk-ant- 开头");
                return (true, "识别为 Anthropic key（sk-ant-…）");
            case Protocol.ChatCompletions:
            case Protocol.Responses:
                if (k.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase))
                    return (false, "sk-ant- 是 Anthropic key，与所选协议不匹配");
                if (k.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
                    return (true, "识别为 sk-…（Bearer 认证）");
                return (true, "自定义 key（非 sk- 前缀，仍按 Bearer 发送）");
            default:
                return (true, "");
        }
    }
}
