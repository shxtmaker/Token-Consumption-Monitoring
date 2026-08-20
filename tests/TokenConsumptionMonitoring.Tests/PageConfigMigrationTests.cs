using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

/// <summary>Phase 0/1：页面配置 envelope 迁移与保护（legacy → schemaVersion 1）。</summary>
public class PageConfigMigrationTests
{
    private const string LegacyArray = """
        [
          {
            "Id": "abc123",
            "Name": "OpenRouter",
            "BaseUrl": "https://openrouter.ai/api/v1",
            "Protocol": 0,
            "Models": ["openrouter/auto", "openai/gpt-4o"],
            "AmountWarnCny": 50.0,
            "AmountCriticalCny": 100.0,
            "TokenWarn": 100000,
            "TokenCritical": 500000,
            "SortOrder": 2
          },
          {
            "Id": "xyz789",
            "Name": "DeepSeek 官方",
            "BaseUrl": "https://platform.deepseek.com",
            "Protocol": 3,
            "Models": [],
            "SortOrder": 0
          }
        ]
        """;

    [Fact]
    public void LegacyArray_MigratesToEnvelope_WithStableId()
    {
        var doc = PageConfigParser.Parse(LegacyArray);

        Assert.False(doc.IsCorrupt);
        Assert.Equal(1, doc.SchemaVersion);
        Assert.Equal(2, doc.Pages.Count);

        var first = doc.Pages[0];
        Assert.Equal("abc123", first.Id);                              // 页面身份稳定保留
        Assert.Equal("OpenRouter", first.Name);
        Assert.Equal("https://openrouter.ai/api/v1", first.BaseUrl);
        Assert.Equal("ChatCompletions", first.Protocol);
        Assert.Equal(new[] { "openrouter/auto", "openai/gpt-4o" }, first.ConfiguredModelHints);   // Models → hints
        Assert.Equal(2, first.SortOrder);                              // 顺序保留
        Assert.Equal(CredentialRefKind.LegacyPageApiKey, first.CredentialRef!.Kind);              // 旧页面 key target
        Assert.Equal(Legacy.ApiKeyTarget("abc123"), first.CredentialRef.Target);
        Assert.NotNull(first.Deprecated);
        Assert.Equal(50.0m, first.Deprecated!.AmountWarnCny);          // 旧阈值进入废弃区
        Assert.Equal(500000, first.Deprecated.TokenCritical);
    }

    [Fact]
    public void Legacy_DeepSeekConsole_Page_HasNoApiKeyRef()
    {
        var doc = PageConfigParser.Parse(LegacyArray);
        var second = doc.Pages[1];   // Protocol=3 (DeepSeekConsole)
        Assert.Equal("DeepSeekConsole", second.Protocol);
        Assert.Equal(CredentialRefKind.None, second.CredentialRef!.Kind);
    }

    [Fact]
    public void UnknownSchemaVersion_IsCorrupt_AndPreserved()
    {
        const string future = """{ "schemaVersion": 99, "pages": [] }""";
        var doc = PageConfigParser.Parse(future);

        Assert.True(doc.IsCorrupt);
        Assert.Contains("99", doc.Diagnostic!);
        // 不覆盖原文件：调用方只读诊断，不写回
        Assert.Empty(doc.Pages);
    }

    [Fact]
    public void CorruptJson_IsCorrupt()
    {
        var doc = PageConfigParser.Parse("{{{ not json ]");
        Assert.True(doc.IsCorrupt);
    }

    [Fact]
    public void MissingSchemaVersion_IsCorrupt()
    {
        var doc = PageConfigParser.Parse("""{ "pages": [] }""");
        Assert.True(doc.IsCorrupt);
    }

    [Fact]
    public void DuplicatePageIds_KeepFirstAndFlagDiagnostic()
    {
        var json = """
            {
              "schemaVersion": 1,
              "pages": [
                { "Id": "dup", "Name": "A", "BaseUrl": "https://a.example" },
                { "Id": "dup", "Name": "B", "BaseUrl": "https://b.example" }
              ]
            }
            """;
        var doc = PageConfigParser.Parse(json);

        Assert.Single(doc.Pages);
        Assert.Equal("A", doc.Pages[0].Name);
        Assert.Contains("重复页面 Id", doc.Diagnostic!);
    }

    [Fact]
    public void Envelope_RoundTrip_ReflectsMethodAgnosticConfig()
    {
        var doc = new PageConfigDocument
        {
            SchemaVersion = 1,
            Pages = new List<PageConfigRecord>
            {
                new()
                {
                    Id = "p1",
                    Name = "OpenCode",
                    BaseUrl = "https://opencode.ai",
                    Protocol = "ChatCompletions",
                    ConfiguredModelHints = new List<string> { "model-a" },
                    CredentialRef = CredentialReference.LegacyPageApiKey("p1"),
                },
            },
        };

        var json = JsonSerializer.Serialize(doc);
        var parsed = PageConfigParser.Parse(json);

        Assert.False(parsed.IsCorrupt);
        var page = parsed.Pages[0];
        Assert.Equal("p1", page.Id);
        Assert.Equal("OpenCode", page.Name);
        Assert.Equal(CredentialRefKind.LegacyPageApiKey, page.CredentialRef!.Kind);
        Assert.Equal("opencode", CredentialResolver.ProviderOf(page.BaseUrl));
    }
}
