using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;

namespace TokenConsumptionMonitoring.UI;

/// <summary>页面编辑草稿及保存校验，不依赖窗口或控件。</summary>
public sealed class PageEditorViewModel
{
    private static readonly string[] CompatibilityMethods =
    {
        "opencode.rolling-window.api-key", "opencode.allowance.oauth",
        "commandcode.allowance-window.compat", "deepseek.console-usage.compat",
        "fireworks.balance.console",
    };
    public PageConfigRecord? Editing { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public KeyFormat.Protocol Protocol { get; set; }
    public CredentialClass KeyClass { get; set; } = CredentialClass.ApiKey;
    public bool EnableCompatibility { get; set; }
    public List<string> Models { get; } = new();
    public string LoadedSecret { get; set; } = "";
    public string? Error { get; private set; }
    public bool IsSaving { get; private set; }
    public string? SavedPageId { get; private set; }

    public PageConfigurationSaveResult Save(PageConfigurationCommands commands, string secret)
    {
        if (IsSaving) return new(false, "正在保存");
        Error = null;
        SavedPageId = null;
        if (string.IsNullOrWhiteSpace(Name)) return Invalid("请输入名称（如：DeepSeek 官方）");
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return Invalid("请输入有效的 HTTP 或 HTTPS Base URL");
        var page = Editing is null ? new PageConfigRecord() : PageCatalog.Copy(Editing);
        page.Name = Name.Trim();
        page.BaseUrl = BaseUrl.Trim();
        page.Protocol = Protocol.ToString();
        page.ConfiguredModelHints = new(Models);
        page.EnabledCompatibilityMethods = EnableCompatibility
            ? Editing is { EnabledCompatibilityMethods.Count: > 0 }
                ? new(Editing.EnabledCompatibilityMethods) : CompatibilityMethods.ToList()
            : new();
        string? newSecret = null;
        if (KeyClass == CredentialClass.LocalRecord)
        {
            page.CredentialRef = CredentialReference.LocalRecord;
            if (!Services.QueryMethods.CodexAccountMethod.Matches(page))
                return Invalid("本机 Codex 登录请使用 https://chatgpt.com");
            page.Protocol = KeyFormat.Protocol.ChatCompletions.ToString();
        }
        else if (Protocol == KeyFormat.Protocol.DeepSeekConsole)
            page.CredentialRef = CredentialReference.GlobalConsoleSession(AppIdentity.DeepSeekCookiesTarget);
        else
        {
            if (!CredentialReference.IsSecretKey(KeyClass)) return Invalid("请选择有效的凭据类型");
            var existingKey = Editing?.CredentialRef.ResolveClass() == KeyClass;
            if (!existingKey && Editing is not null && secret.Length > 0 && secret == LoadedSecret)
                return Invalid("切换凭据类型后，请输入对应类型的新密钥");
            if (secret.Length == 0 && !existingKey) return Invalid("请输入 API Key");
            page.CredentialRef = existingKey ? Editing!.CredentialRef : CredentialReference.SecretKey(AppIdentity.ApiKeyTarget(page.Id), KeyClass);
            if (secret.Length > 0)
            {
                var (valid, hint) = (!string.IsNullOrWhiteSpace(secret) && !secret.Any(char.IsWhiteSpace),
                    "密钥不能包含空白字符");
                if (!valid) return Invalid($"API Key 无效：{hint}");
                if (!existingKey || secret != LoadedSecret) newSecret = secret;
            }
        }
        IsSaving = true;
        try
        {
            var result = commands.Save(page, Editing?.Revision, newSecret);
            Error = result.Diagnostic;
            if (result.Succeeded) SavedPageId = page.Id;
            return result;
        }
        finally { IsSaving = false; }
    }

    private PageConfigurationSaveResult Invalid(string error)
    {
        Error = error;
        return new(false, error);
    }
}
