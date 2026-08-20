using System.Text.Json;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services;

/// <summary>页面配置 JSON 解析/迁移（纯逻辑，无文件 I/O）：legacy 数组 → envelope，未知 schema 保护。</summary>
public static class PageConfigParser
{
    /// <summary>读取容忍大小写混用（旧文件 PascalCase 与新格式 camelCase 兼容）。</summary>
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>解析 pages.json 文本；不可解析/未知 schema 时返回 IsCorrupt 文档（不发散、不覆盖原文件）。</summary>
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

            // 根数组 = legacy schema（迁移为 schemaVersion 1，保留原 Page.Id）
            if (root.ValueKind == JsonValueKind.Array)
            {
                var legacy = JsonSerializer.Deserialize<List<Page>>(text, Options);
                if (legacy is null) return Corrupt("pages.json legacy 数组解析失败");
                doc.SchemaVersion = PageConfigDocument.CurrentSchemaVersion;
                doc.Pages = legacy.Select(PageConfigMigrator.FromLegacy).ToList();
                DropDuplicateIds(doc);
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

            var parsed = JsonSerializer.Deserialize<PageConfigDocument>(text, Options);
            if (parsed is null) return Corrupt("pages.json envelope 解析失败");
            doc = parsed;
            DropDuplicateIds(doc);
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

    private static void DropDuplicateIds(PageConfigDocument document)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dup = false;
        var keep = new List<PageConfigRecord>(document.Pages.Count);
        foreach (var p in document.Pages)
        {
            if (seen.Add(p.Id)) keep.Add(p); else dup = true;
        }
        if (dup)
        {
            document.Pages = keep;
            document.Diagnostic = document.Diagnostic is null
                ? "pages.json 存在重复页面 Id（保留首个，其余丢弃）"
                : document.Diagnostic + "；且存在重复页面 Id（保留首个）";
        }
    }
}
