using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;

namespace TokenConsumptionMonitoring.Models;

/// <summary>废弃配置：旧金额/token 阈值保留但不自动启用。</summary>
public sealed class DeprecatedPageSettings
{
    public decimal? AmountWarnCny { get; set; }
    public decimal? AmountCriticalCny { get; set; }
    public long? TokenWarn { get; set; }
    public long? TokenCritical { get; set; }

    public bool HasValues => AmountWarnCny is not null || AmountCriticalCny is not null || TokenWarn is not null || TokenCritical is not null;
}

/// <summary>
/// 方法无关的页面配置：保存名称、端点、协议、凭据引用、顺序、显示设置与配置提示。
/// 不保存 AdapterKind、套餐选择或固定查询方法作为页面身份。
/// </summary>
public sealed class PageConfigRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>页面名称（= 小组件名称）。</summary>
    public string Name { get; set; } = "";

    /// <summary>Base URL（仅作为低置信度提示，不再决定方法）。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>API 协议名（KeyFormat.Protocol 枚举名）。</summary>
    public string Protocol { get; set; } = KeyFormat.Protocol.ChatCompletions.ToString();

    /// <summary>凭据引用（不含秘密）。</summary>
    public CredentialReference CredentialRef { get; set; } = CredentialReference.None;

    /// <summary>配置提示（模型/端点辅助输入；不参与方法选择与监控展示）。</summary>
    public List<string> ConfiguredModelHints { get; set; } = new();

    /// <summary>页面显示顺序。</summary>
    public int SortOrder { get; set; }

    /// <summary>废弃配置（旧阈值迁移后保留但不启用）。</summary>
    public DeprecatedPageSettings? Deprecated { get; set; }

    public KeyFormat.Protocol ParseProtocol() =>
        Enum.TryParse<KeyFormat.Protocol>(Protocol, out var p) ? p : KeyFormat.Protocol.ChatCompletions;

    /// <summary>兼容桥接：映射回旧 Page 模型（迁移/编辑表单使用）。</summary>
    public Page ToLegacy()
    {
        var p = new Page { Id = Id, Name = Name, BaseUrl = BaseUrl, SortOrder = SortOrder };
        p.Protocol = ParseProtocol();
        p.Models = new List<string>(ConfiguredModelHints);
        if (Deprecated is { } d)
        {
            p.AmountWarnCny = d.AmountWarnCny;
            p.AmountCriticalCny = d.AmountCriticalCny;
            p.TokenWarn = d.TokenWarn;
            p.TokenCritical = d.TokenCritical;
        }
        return p;
    }
}

/// <summary>
/// pages.json 文件级 envelope：带 schemaVersion 的版本化页面配置文档。
/// 根节点为数组视为 legacy schema，迁移为 schemaVersion 1。
/// </summary>
public sealed class PageConfigDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<PageConfigRecord> Pages { get; set; } = new();

    /// <summary>加载失败时的脱敏诊断（未知 schema/损坏 JSON）；不覆盖原文件。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Diagnostic { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCorrupt => !string.IsNullOrEmpty(Diagnostic);
}

/// <summary>页面配置迁移器：legacy List&lt;Page&gt; → versioned envelope。</summary>
public static class PageConfigMigrator
{
    /// <summary>迁移 legacy Page 文档（根数组）为 PageConfigRecord。</summary>
    public static PageConfigRecord FromLegacy(Page legacy)
    {
        return new PageConfigRecord
        {
            Id = legacy.Id,                       // 保留原 Id，不从名称/URL/key 重新生成
            Name = legacy.Name,
            BaseUrl = legacy.BaseUrl,
            Protocol = legacy.Protocol.ToString(),
            ConfiguredModelHints = new List<string>(legacy.Models),
            SortOrder = legacy.SortOrder,
            Deprecated = new DeprecatedPageSettings
            {
                AmountWarnCny = legacy.AmountWarnCny,
                AmountCriticalCny = legacy.AmountCriticalCny,
                TokenWarn = legacy.TokenWarn,
                TokenCritical = legacy.TokenCritical,
            },
            CredentialRef = legacy.NeedsKey
                ? CredentialReference.LegacyPageApiKey(legacy.Id)
                : CredentialReference.None,
        };
    }

    /// <summary>把 legacy 模型映射回 Page（编辑/展示兼容用）。</summary>
    public static Page ToLegacy(PageConfigRecord record) => record.ToLegacy();
}
