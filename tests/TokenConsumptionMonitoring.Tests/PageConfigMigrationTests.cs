using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class PageConfigMigrationTests
{
    private const string RootArray = """
        [
          {
            "Id": "abc123",
            "Name": "DeepSeek",
            "BaseUrl": "https://api.deepseek.com",
            "Protocol": 0,
            "Models": ["deepseek-chat"],
            "SortOrder": 2
          }
        ]
        """;

    [Fact]
    public void RootArray_MigratesToEnvelope_WithNewCredentialReference()
    {
        var document = PageConfigParser.Parse(RootArray);

        Assert.False(document.IsCorrupt);
        Assert.True(document.RequiresSchemaRewrite);
        var page = Assert.Single(document.Pages);
        Assert.Equal("abc123", page.Id);
        Assert.Equal("ChatCompletions", page.Protocol);
        Assert.Equal(CredentialRefKind.PageApiKey, page.CredentialRef.Kind);
        Assert.Equal("TokenConsumptionMonitoring.ApiKey.abc123", page.CredentialRef.Target);
    }

    [Fact]
    public void FutureSchema_IsRecoveryRequired()
    {
        var document = PageConfigParser.Parse("""{ "schemaVersion": 99, "pages": [] }""");

        Assert.True(document.IsCorrupt);
        Assert.Contains("99", document.Diagnostic!);
    }

    [Fact]
    public void DuplicateIds_AreRecoveryRequired_AndRecordsAreNotDropped()
    {
        var document = PageConfigParser.Parse("""
            {
              "schemaVersion": 1,
              "pages": [
                { "id": "dup", "name": "A", "baseUrl": "https://a.invalid", "protocol": "ChatCompletions", "credentialRef": { "kind": "None" }, "enabledCompatibilityMethods": [] },
                { "id": "dup", "name": "B", "baseUrl": "https://b.invalid", "protocol": "ChatCompletions", "credentialRef": { "kind": "None" }, "enabledCompatibilityMethods": [] }
              ]
            }
            """);

        Assert.True(document.IsCorrupt);
        Assert.Equal(2, document.Pages.Count);
        Assert.Contains("重复页面 Id", document.Diagnostic!);
    }

    [Fact]
    public void MissingRequiredField_IsRecoveryRequired()
    {
        var document = PageConfigParser.Parse("""
            { "schemaVersion": 1, "pages": [{ "id": "p", "name": "missing-url" }] }
            """);

        Assert.True(document.IsCorrupt);
    }

    [Fact]
    public void RecoveryState_RejectsSaveAndLeavesOriginalBytesUntouched()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "pages.json");
            const string original = "{\"schemaVersion\":99,\"pages\":[]}";
            File.WriteAllText(path, original);
            var store = new PageConfigStore(directory);
            var load = store.Load();

            Assert.Equal(PageConfigurationLoadState.RecoveryRequired, load.State);
            Assert.Null(load.WriteLease);
            var save = store.Save(new PageConfigDocument());

            Assert.False(save.Succeeded);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadyState_ProvidesLeaseAndWritesAtomically()
    {
        var directory = NewDirectory();
        try
        {
            var store = new PageConfigStore(directory);
            var load = store.Load();
            var save = store.Save(new PageConfigDocument
            {
                Pages = new List<PageConfigRecord>
                {
                    new()
                    {
                        Id = "p",
                        Name = "page",
                        BaseUrl = "https://example.invalid",
                        Protocol = "ChatCompletions",
                        CredentialRef = CredentialReference.None,
                    },
                },
            }, load.WriteLease!);

            Assert.True(save.Succeeded);
            Assert.True(File.Exists(Path.Combine(directory, "pages.json")));
            Assert.False(File.Exists(Path.Combine(directory, "pages.json.tmp")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProviderResolver_IsOnlyAConfigurationHint()
    {
        Assert.Equal("deepseek", CredentialResolver.ProviderOf("https://api.deepseek.com"));
        Assert.Null(CredentialResolver.ProviderOf("https://example.invalid"));
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_config_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
