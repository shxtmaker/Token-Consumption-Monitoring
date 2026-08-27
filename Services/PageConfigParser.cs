using System.Text.Json;
using System.Text.Json.Serialization;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services;

/// <summary>页面配置 JSON 解析/迁移（纯逻辑，无文件 I/O）：根数组 → envelope，未知 schema 保护。</summary>
public static class PageConfigParser
{
    /// <summary>读取容忍大小写混用（旧文件 PascalCase 与新格式 camelCase 兼容）。</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>解析 pages.json 文本；不可解析/未知 schema 时返回 IsCorrupt 文档（不扩散、不覆盖原文件）。</summary>
    public static PageConfigDocument Parse(string text)
    {
        var doc = new PageConfigDocument();
        if (string.IsNullOrWhiteSpace(text))
        {
            doc.Diagnostic = "pages.json 内容为空";
            return doc;
        }
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;

            // 根数组是旧结构（迁移为 schemaVersion 1，保留原 Page.Id）。
            if (root.ValueKind == JsonValueKind.Array)
            {
                var legacy = JsonSerializer.Deserialize<List<Page>>(text, Options);
                if (legacy is null) return Corrupt("pages.json legacy 数组解析失败");
                if (legacy.Any(page => page is null)) return Corrupt("pages.json legacy 数组存在空页面记录");
                doc.SchemaVersion = PageConfigDocument.CurrentSchemaVersion;
                doc.Pages = legacy.Select(PageConfigMigrator.FromLegacy).ToList();
                doc.RequiresSchemaRewrite = true;
                ValidatePages(doc);
                return doc;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                doc.Diagnostic = "pages.json 根节点必须是对象或 legacy 数组（原文件保留，未覆盖）";
                return doc;
            }
            int version = 0;
            var hasVersion = TryGetCaseInsensitive(root, "schemaVersion", out var schemaEl)
                             && schemaEl.ValueKind == JsonValueKind.Number
                             && schemaEl.TryGetInt32(out version);
            if (!hasVersion)
            {
                doc.Diagnostic = "pages.json 缺少可解析的 schemaVersion（原文件保留，未覆盖）";
                return doc;
            }
            if (version != PageConfigDocument.CurrentSchemaVersion)
            {
                doc.Diagnostic = $"pages.json schemaVersion={version} 超出当前支持的 {PageConfigDocument.CurrentSchemaVersion}（原文件保留，未覆盖）";
                return doc;
            }

            if (!ValidateEnvelopeShape(root, out var shapeDiagnostic))
                return Corrupt(shapeDiagnostic);

            var parsed = JsonSerializer.Deserialize<PageConfigDocument>(text, Options);
            if (parsed is null) return Corrupt("pages.json envelope 解析失败");
            doc = parsed;
            ValidatePages(doc);
            return doc;
        }
        catch (JsonException)
        {
            return Corrupt("pages.json 内容无法解析（原文件保留，未覆盖）");
        }
    }

    private static bool TryGetCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }

    private static PageConfigDocument Corrupt(string diag) =>
        new() { SchemaVersion = PageConfigDocument.CurrentSchemaVersion, Diagnostic = diag };

    private static void ValidatePages(PageConfigDocument document)
    {
        if (document.Pages is null)
        {
            document.Diagnostic = "pages.json 缺少 pages 数组（原文件保留，未覆盖）";
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in document.Pages)
        {
            if (p is null)
            {
                document.Diagnostic = "pages.json 存在空页面记录（原文件保留，未覆盖）";
                continue;
            }
            if (string.IsNullOrWhiteSpace(p.Id)
                || string.IsNullOrWhiteSpace(p.Name)
                || string.IsNullOrWhiteSpace(p.BaseUrl)
                || string.IsNullOrWhiteSpace(p.Protocol)
                || p.CredentialRef is null
                || p.ConfiguredModelHints is null
                || p.EnabledCompatibilityMethods is null
                || !Enum.TryParse<KeyFormat.Protocol>(p.Protocol, ignoreCase: true, out var protocol)
                || !Enum.IsDefined(typeof(KeyFormat.Protocol), protocol)
                || !Enum.IsDefined(typeof(CredentialRefKind), p.CredentialRef?.Kind ?? (CredentialRefKind)(-1))
                || RequiresTarget(p.CredentialRef))
            {
                document.Diagnostic = "pages.json 存在缺失或无效的页面必填字段（原文件保留，未覆盖）";
            }
            if (!string.IsNullOrWhiteSpace(p.Id) && !seen.Add(p.Id))
            {
                document.Diagnostic = "pages.json 存在重复页面 Id（原文件保留，未覆盖）";
            }
        }
    }

    /// <summary>验证 envelope 的字段存在性，避免模型默认值掩盖损坏或不完整文件。</summary>
    private static bool ValidateEnvelopeShape(JsonElement root, out string diagnostic)
    {
        diagnostic = "";
        if (!TryGetCaseInsensitive(root, "pages", out var pages))
        {
            diagnostic = "pages.json 缺少 pages 数组（原文件保留，未覆盖）";
            return false;
        }
        if (pages.ValueKind != JsonValueKind.Array)
        {
            diagnostic = "pages.json pages 必须是数组（原文件保留，未覆盖）";
            return false;
        }

        foreach (var page in pages.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object)
            {
                diagnostic = "pages.json 存在非对象页面记录（原文件保留，未覆盖）";
                return false;
            }

            foreach (var required in new[] { "id", "name", "baseUrl", "protocol", "credentialRef" })
            {
                if (!TryGetCaseInsensitive(page, required, out var value)
                    || value.ValueKind == JsonValueKind.Null)
                {
                    diagnostic = $"pages.json 页面缺少 {required}（原文件保留，未覆盖）";
                    return false;
                }
            }

            if (!TryGetCaseInsensitive(page, "credentialRef", out var credential)
                || credential.ValueKind != JsonValueKind.Object
                || !TryGetCaseInsensitive(credential, "kind", out var kind)
                || kind.ValueKind == JsonValueKind.Null)
            {
                diagnostic = "pages.json 页面 credentialRef 不完整（原文件保留，未覆盖）";
                return false;
            }
        }
        return true;
    }

    private static bool RequiresTarget(CredentialReference? reference)
        => reference is not null
           && (reference.Kind is CredentialRefKind.PageApiKey
               or CredentialRefKind.ApiKeyTarget
               or CredentialRefKind.GlobalOAuth
               or CredentialRefKind.GlobalConsoleSession)
           && string.IsNullOrWhiteSpace(reference.Target);

    /// <summary>保存前验证由代码构造的文档；解析时的结构验证仍由 Parse 负责。</summary>
    public static bool ValidateForSave(PageConfigDocument document)
    {
        if (document.Pages is null)
        {
            document.Diagnostic = "页面配置缺少 pages 数组";
            return false;
        }
        ValidatePages(document);
        return !document.IsCorrupt;
    }
}
