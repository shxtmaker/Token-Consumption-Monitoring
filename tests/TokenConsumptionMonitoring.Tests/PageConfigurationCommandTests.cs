using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class PageConfigurationCommandTests
{
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":99,\"pages\":[]}")]
    public void RealRecovery_RejectsCommandsBeforeCredentialWriteAndPreservesBackup(string damaged)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_recovery_" + Guid.NewGuid().ToString("N"));
        var secrets = new Secrets();
        try
        {
            var store = new PageConfigStore(directory);
            var catalog = new PageCatalog(store.Load().Document.Pages);
            var commands = new PageConfigurationCommands(catalog, store, secrets);
            var page = new PageConfigRecord { Name = "original", BaseUrl = "https://example.invalid" };
            Assert.True(commands.Save(page, null, "original-secret").Succeeded);
            var saved = catalog.Snapshot().Single();
            Assert.True(commands.Save(saved, saved.Revision, null).Succeeded);
            var path = Path.Combine(directory, "pages.json");
            var backupPath = path + ".bak";
            var backupBytes = File.ReadAllBytes(backupPath);
            File.WriteAllText(path, damaged);
            var originalBytes = File.ReadAllBytes(path);
            var recoveryStore = new PageConfigStore(directory);
            var recovery = recoveryStore.Load();
            Assert.True(recovery.IsRecoveryRequired);
            var recoveryCatalog = new PageCatalog(recovery.Document.Pages);
            var recoveryCommands = new PageConfigurationCommands(recoveryCatalog, recoveryStore, secrets);
            var writes = secrets.Writes;
            var failedSave = recoveryCommands.Save(page, null, "replacement-secret");
            Assert.False(failedSave.Succeeded);
            Assert.True(failedSave.RecoveryRequired);
            Assert.False(string.IsNullOrWhiteSpace(failedSave.Diagnostic));
            Assert.False(recoveryCommands.Delete(saved).Succeeded);
            Assert.Equal(writes, secrets.Writes);
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Equal(backupBytes, File.ReadAllBytes(backupPath));
            Assert.Equal("original-secret", secrets.Values[saved.CredentialRef.Target!]);

            // Explicit recovery in this isolated fixture; normal Save must never perform it.
            File.Copy(backupPath, path, overwrite: true);
            var restoredStore = new PageConfigStore(directory);
            var restored = restoredStore.Load();
            Assert.False(restored.IsRecoveryRequired);
            var restoredCatalog = new PageCatalog(restored.Document.Pages);
            var restoredCommands = new PageConfigurationCommands(restoredCatalog, restoredStore, secrets);
            var restoredPage = restoredCatalog.Snapshot().Single();
            Assert.Equal("original", restoredPage.Name);
            Assert.Equal("original-secret", restoredCommands.ReadApiKeyForEditing(restoredPage));
            restoredPage.Name = "recovered";
            Assert.True(restoredCommands.Save(restoredPage, restoredPage.Revision, null).Succeeded);
            Assert.Equal("recovered", new PageConfigStore(directory).Load().Document.Pages.Single().Name);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Editor_ReloadsVersionedSecretAndUnchangedSaveKeepsReference()
    {
        var secrets = new Secrets();
        var persistence = new Persistence(secrets);
        var catalog = new PageCatalog(Array.Empty<PageConfigRecord>());
        var commands = new PageConfigurationCommands(catalog, persistence, secrets);
        var draft = new PageConfigRecord { Name = "test", BaseUrl = "https://example.invalid" };
        const string key = "sk-test-only-not-a-real-key-123456789";
        Assert.True(commands.Save(draft, null, key).Succeeded);
        var saved = catalog.Snapshot().Single();
        var loaded = commands.ReadApiKeyForEditing(saved);
        Assert.Equal(key, loaded);
        var editor = new TokenConsumptionMonitoring.UI.PageEditorViewModel
        {
            Editing = saved, Name = "renamed", BaseUrl = saved.BaseUrl,
            Protocol = KeyFormat.Protocol.ChatCompletions, LoadedSecret = loaded,
        };
        Assert.True(editor.Save(commands, loaded).Succeeded);
        Assert.Equal(saved.CredentialRef, catalog.Snapshot().Single().CredentialRef);
        Assert.Equal(1, secrets.Writes);
        Assert.Equal("renamed", catalog.Snapshot().Single().Name);
        secrets.Values.Clear();
        Assert.Equal("", commands.ReadApiKeyForEditing(saved));
    }

    [Fact]
    public void RealFile_ReloadAndBackupRetainExistingCredentialReferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_config_" + Guid.NewGuid().ToString("N"));
        var secrets = new Secrets();
        try
        {
            var store = new PageConfigStore(directory);
            var catalog = new PageCatalog(store.Load().Document.Pages);
            var commands = new PageConfigurationCommands(catalog, store, secrets);
            var page = new PageConfigRecord { Name = "first", BaseUrl = "https://example.invalid" };
            Assert.True(commands.Save(page, null, "first-secret").Succeeded);
            var first = catalog.Snapshot().Single();
            var second = PageCatalog.Copy(first);
            second.Name = "second";
            Assert.True(commands.Save(second, second.Revision, "second-secret").Succeeded);
            var reloaded = new PageConfigStore(directory).Load().Document.Pages.Single();
            Assert.Equal("second-secret", secrets.Values[reloaded.CredentialRef.Target!]);
            var backup = PageConfigParser.Parse(File.ReadAllText(Path.Combine(directory, "pages.json.bak")));
            Assert.False(backup.IsCorrupt);
            Assert.Equal("first-secret", secrets.Values[backup.Pages.Single().CredentialRef.Target!]);
            Assert.Equal("first", backup.Pages.Single().Name);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Editor_ConsoleToApiRequiresKeyBeforeWriting()
    {
        var secrets = new Secrets();
        var persistence = new Persistence(secrets);
        var original = new PageConfigRecord
        {
            CredentialRef = CredentialReference.GlobalConsoleSession("console"),
        };
        var catalog = new PageCatalog(new[] { original });
        var editor = new TokenConsumptionMonitoring.UI.PageEditorViewModel
        {
            Editing = catalog.Snapshot().Single(), Name = "api", BaseUrl = "https://example.invalid",
            Protocol = KeyFormat.Protocol.ChatCompletions,
        };
        var result = editor.Save(new PageConfigurationCommands(catalog, persistence, secrets), "");
        Assert.False(result.Succeeded);
        Assert.Equal("请输入 API Key", editor.Error);
        Assert.Equal(0, secrets.Writes);
        Assert.Null(persistence.Saved);
        Assert.False(editor.IsSaving);
    }

    [Theory]
    [InlineData(CredentialClass.AdminKey)]
    [InlineData(CredentialClass.ManagementKey)]
    [InlineData(CredentialClass.ServiceAccountKey)]
    public void TypedKeys_PreserveClassAcrossSaveEditAndReload(CredentialClass kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_typed_" + Guid.NewGuid().ToString("N"));
        var secrets = new Secrets();
        try
        {
            var store = new PageConfigStore(directory);
            var catalog = new PageCatalog(store.Load().Document.Pages);
            var commands = new PageConfigurationCommands(catalog, store, secrets);
            var editor = new TokenConsumptionMonitoring.UI.PageEditorViewModel
            { Name = "Organization", BaseUrl = "https://api.openai.com", KeyClass = kind };
            Assert.True(editor.Save(commands, "test-admin-secret").Succeeded);
            var page = catalog.Snapshot().Single();
            Assert.Equal(kind, page.CredentialRef.ResolveClass());
            Assert.Equal("test-admin-secret", commands.ReadApiKeyForEditing(page));
            Assert.Equal(kind, new PageConfigStore(directory).Load().Document.Pages.Single().CredentialRef.ResolveClass());
            editor.Editing = page;
            editor.LoadedSecret = "test-admin-secret";
            editor.Name = "Renamed";
            Assert.True(editor.Save(commands, "test-admin-secret").Succeeded);
            Assert.Equal(1, secrets.Writes);
            Assert.Equal(page.CredentialRef, catalog.Snapshot().Single().CredentialRef);
            editor.Editing = catalog.Snapshot().Single();
            editor.KeyClass = CredentialClass.ApiKey;
            Assert.False(editor.Save(commands, "test-admin-secret").Succeeded);
            Assert.Equal(1, secrets.Writes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ThirdPartyAnthropicProtocol_DoesNotRequireAnAnthropicBrandedKey()
    {
        var secrets = new Secrets();
        var catalog = new PageCatalog([]);
        var editor = new TokenConsumptionMonitoring.UI.PageEditorViewModel
        {
            Name = "Z.ai", BaseUrl = "https://api.z.ai/api/anthropic",
            Protocol = KeyFormat.Protocol.Anthropic,
        };
        Assert.True(editor.Save(new PageConfigurationCommands(catalog, new Persistence(secrets), secrets), "vendor-specific-key").Succeeded);
        Assert.Equal(CredentialClass.ApiKey, catalog.Snapshot().Single().CredentialRef.ResolveClass());
    }

    private sealed class Secrets : IPageCredentialStore
    {
        public bool TryRead(string target, out string? secret) => Values.TryGetValue(target, out secret);
        public readonly Dictionary<string, string> Values = new();
        public bool Fail;
        public int Writes;
        public void Write(string target, string secret)
        {
            Writes++;
            if (Fail) throw new IOException("simulated");
            Values[target] = secret;
        }
        public void Delete(string target) => Values.Remove(target);
    }

    private sealed class Persistence(Secrets secrets) : IPageConfigurationPersistence
    {
        public bool Recovery;
        public bool Fail;
        public PageConfigDocument? Saved;
        public PageConfigurationSaveResult ValidateWrite(PageConfigDocument document) => new(!Recovery);
        public PageConfigurationSaveResult Save(PageConfigDocument document)
        {
            Assert.True(secrets.Values.ContainsKey(document.Pages.Single().CredentialRef.Target!));
            if (Fail) return new(false);
            Saved = document;
            return new(true);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void FailedOperation_PreservesOldDirectoryAndSecret(bool recovery, bool secretFailure, bool configFailure)
    {
        var secrets = new Secrets { Fail = secretFailure };
        secrets.Values["old"] = "old-secret";
        var persistence = new Persistence(secrets) { Recovery = recovery, Fail = configFailure };
        var catalog = new PageCatalog(new[] { new PageConfigRecord { CredentialRef = CredentialReference.ApiKeyTarget("old") } });
        var draft = catalog.Snapshot().Single();
        var result = new PageConfigurationCommands(catalog, persistence, secrets).Save(draft, draft.Revision, "new-secret");
        Assert.False(result.Succeeded);
        Assert.Equal("old", catalog.Snapshot().Single().CredentialRef.Target);
        Assert.True(catalog.IsCurrent(draft.Id, draft.Revision));
        Assert.Single(secrets.Values);
        Assert.Equal("old-secret", secrets.Values["old"]);
        if (recovery) Assert.Equal(0, secrets.Writes);
    }

    [Fact]
    public void SuccessfulSave_PersistsNewReferenceBeforePublishingAndRetainsBackupSecret()
    {
        var secrets = new Secrets();
        secrets.Values["old"] = "old-secret";
        var persistence = new Persistence(secrets);
        var catalog = new PageCatalog(new[] { new PageConfigRecord { CredentialRef = CredentialReference.ApiKeyTarget("old") } });
        var draft = catalog.Snapshot().Single();
        var commands = new PageConfigurationCommands(catalog, persistence, secrets);
        Assert.True(commands.Save(draft, draft.Revision, "new-secret").Succeeded);
        var saved = catalog.Snapshot().Single();
        Assert.Equal(saved.CredentialRef, persistence.Saved!.Pages.Single().CredentialRef);
        Assert.Equal("new-secret", secrets.Values[saved.CredentialRef.Target!]);
        Assert.Equal("old-secret", secrets.Values["old"]);
        Assert.True(commands.Save(saved, saved.Revision, null).Succeeded);
        Assert.Equal(1, secrets.Writes);
        Assert.Equal(saved.CredentialRef, catalog.Snapshot().Single().CredentialRef);
    }
}
