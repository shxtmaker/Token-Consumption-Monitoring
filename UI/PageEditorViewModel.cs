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
        "opendesign.windows.console",
    };
    public PageConfigRecord? Editing { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public KeyFormat.Protocol Protocol { get; set; }
    public CredentialClass KeyClass { get; set; } = CredentialClass.ApiKey;
    public bool EnableCompatibility { get; set; }
    public string OpenDesignWorkspaceInput { get; set; } = "";
    public string? QuotaReferenceModelId { get; set; }
    public string? QuotaReferencePlanId { get; set; }
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
        if (!string.IsNullOrWhiteSpace(QuotaReferenceModelId)
            && Services.QuotaReferences.OfficialQuotaCatalogService.ProviderOf(BaseUrl.Trim()) is { } referenceProvider)
        {
            if (!QuotaReferenceModelId.StartsWith(referenceProvider + ":", StringComparison.Ordinal) || QuotaReferenceModelId.Length > 200)
                return Invalid("请选择当前供应商的额度估算模型");
            if (QuotaReferencePlanId is not ("go" or "goat" or "plus" or "pro" or "max" or "ultra"))
                return Invalid("请选择参考套餐");
        }
        var page = Editing is null ? new PageConfigRecord() : PageCatalog.Copy(Editing);
        page.Name = Name.Trim();
        page.BaseUrl = BaseUrl.Trim();
        page.Protocol = Protocol.ToString();
        page.ConfiguredModelHints = new(Models);
        page.QuotaReferenceModelId = string.IsNullOrWhiteSpace(QuotaReferenceModelId) ? null : QuotaReferenceModelId;
        page.QuotaReferencePlanId = string.IsNullOrWhiteSpace(QuotaReferencePlanId) ? null : QuotaReferencePlanId;
        page.EnabledCompatibilityMethods = EnableCompatibility
            ? Editing is { EnabledCompatibilityMethods.Count: > 0 }
                ? new(Editing.EnabledCompatibilityMethods) : CompatibilityMethods.ToList()
            : new();
        page.EnabledCompatibilityMethods.RemoveAll(id => id == Services.QueryMethods.OpenDesignConsoleMethod.BalanceMethodId);
        string? newSecret = null;
        if (Services.QueryMethods.OpenDesignConsoleMethod.MatchesApiEndpoint(page.BaseUrl))
        {
            var input = OpenDesignWorkspaceInput.Trim();
            if (input.Length > 0 || EnableCompatibility)
            {
                if (!Services.QueryMethods.OpenDesignConsoleMethod.TryParseWorkspaceInput(input, out var workspace))
                    return Invalid("请填写 OpenDesign 工作空间 ID，或粘贴含 workspaceId 的套餐与余额链接");
                page.OpenDesignWorkspaceId = workspace;
            }
            else page.OpenDesignWorkspaceId = null;
            if (EnableCompatibility)
                foreach (var method in new[] { Services.QueryMethods.OpenDesignConsoleMethod.WindowsMethodId })
                    if (!page.EnabledCompatibilityMethods.Contains(method)) page.EnabledCompatibilityMethods.Add(method);
        }
        else page.OpenDesignWorkspaceId = null;
        if (KeyClass == CredentialClass.ConsoleSession)
        {
            if (!Services.QueryMethods.OpenDesignConsoleMethod.TryGetWorkspace(page.BaseUrl, out _))
                return Invalid("OpenDesign 控制台登录需要填写含 workspaceId 的套餐与余额完整链接");
            if (!EnableCompatibility) return Invalid("请勾选启用兼容额度查询");
            page.CredentialRef = CredentialReference.GlobalConsoleSession($"opendesign:page:{page.Id}");
            page.Protocol = KeyFormat.Protocol.ChatCompletions.ToString();
            foreach (var method in new[] { "opendesign.windows.console" })
                if (!page.EnabledCompatibilityMethods.Contains(method)) page.EnabledCompatibilityMethods.Add(method);
        }
        else if (KeyClass == CredentialClass.LocalRecord)
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
