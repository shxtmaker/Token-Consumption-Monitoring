using System.Net;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.QuotaReferences;
using TokenConsumptionMonitoring.UI;
using TokenConsumptionMonitoring.UI.Diagnostics;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class QuotaReferenceTests
{
    private const string OpenHtml = """
        <div class="dpu-row" role="row"><span><strong>DeepSeek V4.1 Flash</strong></span>
        <span data-usage-value="go">145,600</span><span data-usage-value="go">$120</span>
        <span data-usage-value="plus">$160</span><span data-usage-value="pro">$360</span><span data-usage-value="max">$600</span></div>
        <div class="dpu-row"><strong>GPT-6 Luna</strong><span data-usage-value="go">$15</span></div>
        """;
    private const string CommandHtml = """
        <table><tr><th>Model</th><th>Input</th><th>Monthly credits</th></tr>
        <tr><td>Model A</td><td>$1</td><td>$10</td></tr>
        <tr><td>Model B</td><td>$3</td><td><s>$6</s><span>$10</span> through Oct 20th</td></tr>
        <tr><td>Free model</td><td>Free</td><td>Free</td></tr></table>
        """;

    [Fact]
    public void OpenDesignReadsAmountColumnRatherThanRequestsAndDoesNotAddModels()
    {
        var entries = OfficialQuotaParser.ParseOpenDesign(OpenHtml);
        Assert.Equal(5, entries.Count);
        Assert.Equal(120m, entries.Single(e => e.ModelName == "DeepSeek V4.1 Flash" && e.PlanId == "go").MonthlyUsd);
        Assert.Equal(15m, entries.Single(e => e.ModelName == "GPT-6 Luna").MonthlyUsd);
        Assert.All(entries, e => Assert.Equal(OfficialQuotaCatalogService.OpenDesignPricingUrl, e.SourceUrl));
    }

    [Fact]
    public void CommandCodeUsesDisplayedPromotionAndPreservesNoteWithoutInferringBalance()
    {
        var entries = OfficialQuotaParser.ParseCommandCode(CommandHtml, "go");
        var promotion = entries.Single(e => e.ModelName == "Model B");
        Assert.Equal(10m, promotion.MonthlyUsd);
        Assert.Equal("through Oct 20th", promotion.Note);
        var free = entries.Single(e => e.ModelName == "Free model");
        Assert.True(free.IsFree);
        Assert.Null(free.MonthlyUsd);
    }

    [Fact]
    public void MaxColumnsRemainDistinct()
    {
        var entries = OfficialQuotaParser.ParseCommandCode("""
            <table><tr><th>Model</th><th>Max 10× credits</th><th>Max 20× credits</th></tr>
            <tr><td>Premium</td><td>$100</td><td>$200</td></tr></table>
            """, "max");
        Assert.Equal(100m, entries.Single(e => e.PlanId == "max").MonthlyUsd);
        Assert.Equal("Max 10×", entries.Single(e => e.PlanId == "max").PlanName);
        Assert.Equal(200m, entries.Single(e => e.PlanId == "ultra").MonthlyUsd);
    }

    [Fact]
    public void ConflictingOrMissingWebsiteDataDoesNotBecomeZero()
    {
        Assert.Throws<InvalidDataException>(() => OfficialQuotaParser.ParseOpenDesign("<html>maintenance</html>"));
        Assert.Throws<InvalidDataException>(() => OfficialQuotaParser.ParseOpenDesign(OpenHtml + OpenHtml.Replace("$120", "$99")));
        Assert.Throws<InvalidDataException>(() => OfficialQuotaParser.ParseCommandCode(CommandHtml.Replace("$10</td>", "unknown</td>"), "go"));
        Assert.Throws<InvalidDataException>(() => OfficialQuotaParser.ParseCommandCode(CommandHtml.Replace("$10</td>", "$10 / $20</td>"), "go"));
    }

    [Theory]
    [InlineData("https://amr-link.open-design.ai/v1", "opendesign")]
    [InlineData("https://api.commandcode.ai/provider/v1", "commandcode")]
    [InlineData("https://api.commandcode.ai.evil.invalid/v1", null)]
    [InlineData("https://user@api.commandcode.ai/v1", null)]
    [InlineData("http://api.commandcode.ai/v1", null)]
    public void PublicReferenceDoesNotSendRequestsToCustomEndpoints(string url, string? provider)
        => Assert.Equal(provider, OfficialQuotaCatalogService.ProviderOf(url));

    [Fact]
    public async Task FreshCatalogIsCachedAndNetworkFailureRetainsDatedStaleReference()
    {
        using var directory = new TemporaryDirectory();
        var handler = new Handler { Body = OpenHtml };
        using var service = new OfficialQuotaCatalogService(directory.Path, new HttpClient(handler));
        var first = await service.GetAsync("opendesign", false, default);
        var cached = await service.GetAsync("opendesign", false, default);
        Assert.Equal(first.Version, cached.Version);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("https://open-design.ai/cloud/dashboard", handler.LastUrl);
        Assert.False(handler.HadAuthorization);
        handler.Fail = true;
        var fallback = await service.GetAsync("opendesign", true, default);
        Assert.True(fallback.IsStale);
        Assert.Equal(first.FetchedAt, fallback.FetchedAt);
        Assert.Equal(first.Entries, fallback.Entries);
        Assert.NotNull(fallback.Warning);
        var retry = await service.GetAsync("opendesign", false, default);
        Assert.True(retry.IsStale);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task RestartUsesOnlyValidatedPublicCacheAndCancellationDoesNotPublishFailure()
    {
        using var directory = new TemporaryDirectory();
        using (var initial = new OfficialQuotaCatalogService(directory.Path, new HttpClient(new Handler { Body = OpenHtml })))
            await initial.GetAsync("opendesign", false, default);
        var noNetwork = new Handler { Fail = true };
        using var restarted = new OfficialQuotaCatalogService(directory.Path, new HttpClient(noNetwork));
        Assert.False((await restarted.GetAsync("opendesign", false, default)).IsStale);
        Assert.Equal(0, noNetwork.Calls);
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.GetAsync("opendesign", true, ct.Token));
        Assert.Equal(0, noNetwork.Calls);
    }

    [Fact]
    public async Task CorruptCacheIsIgnoredAndFailedRefreshIsNotZero()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "opendesign-v1.json"),
            "{\"Provider\":\"opendesign\",\"Version\":\"x\",\"Entries\":null}");
        using var service = new OfficialQuotaCatalogService(directory.Path, new HttpClient(new Handler { Fail = true }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync("opendesign", false, default));
    }

    [Fact]
    public void ReferenceSelectionRoundTripsWithoutChangingApiOrLegacyModelHints()
    {
        using var directory = new TemporaryDirectory();
        var store = new PageConfigStore(directory.Path);
        store.Load();
        var original = new PageConfigRecord
        {
            Name = "OpenDesign", BaseUrl = "https://amr-link.open-design.ai/v1", OpenDesignWorkspaceId = "workspace-a",
            CredentialRef = CredentialReference.SecretKey("existing-key", CredentialClass.ApiKey),
            ConfiguredModelHints = ["original-api-model"], EnabledCompatibilityMethods = ["opendesign.balance.console", "opendesign.windows.console"],
        };
        var catalog = new PageCatalog([original]);
        var editor = new PageEditorViewModel
        {
            Editing = catalog.Snapshot().Single(), Name = original.Name, BaseUrl = original.BaseUrl,
            Protocol = original.ParseProtocol(), KeyClass = CredentialClass.ApiKey, EnableCompatibility = true,
            OpenDesignWorkspaceInput = original.OpenDesignWorkspaceId!,
            QuotaReferenceModelId = "opendesign:deepseek v4.1 flash", QuotaReferencePlanId = "go",
        };
        editor.Models.AddRange(original.ConfiguredModelHints);
        var secrets = new NoSecretChanges();
        Assert.True(editor.Save(new PageConfigurationCommands(catalog, store, secrets), "").Succeeded);
        var saved = new PageConfigStore(directory.Path).Load().Document.Pages.Single();
        Assert.Equal(original.BaseUrl, saved.BaseUrl);
        Assert.Equal(original.CredentialRef, saved.CredentialRef);
        Assert.Equal(original.Protocol, saved.Protocol);
        Assert.Equal(original.ConfiguredModelHints, saved.ConfiguredModelHints);
        Assert.Equal(editor.QuotaReferenceModelId, saved.QuotaReferenceModelId);
        Assert.Equal(editor.QuotaReferencePlanId, catalog.Snapshot().Single().QuotaReferencePlanId);
        Assert.DoesNotContain("opendesign.balance.console", saved.EnabledCompatibilityMethods);
        Assert.Equal(0, secrets.Writes);
    }

    [Fact]
    public void ReferencesDoNotBecomeRealCapabilitiesAndSwitchClearsPreviousValue()
    {
        var state = new MonitorState();
        var page = new PageConfigRecord { Name = "OpenDesign", BaseUrl = "https://amr-link.open-design.ai/v1",
            QuotaReferenceModelId = "opendesign:deepseek v4.1 flash", QuotaReferencePlanId = "go" };
        state.QuotaReference.Update(page, new("opendesign", DateTimeOffset.UtcNow, "test", OfficialQuotaParser.ParseOpenDesign(OpenHtml)));
        Assert.Contains("$120", state.QuotaReference.AmountLabel);
        Assert.Contains("估算", state.QuotaReference.Notice);
        Assert.StartsWith("剩余额度：暂不可得", state.QuotaReference.RemainingLabel);
        Assert.False(state.Snapshot.HasCapabilities);
        Assert.Equal(ConnectionStatus.Unknown, state.Connection);
        state.QuotaReference.Configure(new() { Name = "Other", BaseUrl = "https://example.com" });
        Assert.False(state.QuotaReference.IsVisible);
        Assert.Empty(state.QuotaReference.AmountLabel);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Body = "";
        public bool Fail;
        public int Calls;
        public string? LastUrl;
        public bool HadAuthorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri!.ToString();
            HadAuthorization |= request.Headers.Authorization is not null;
            if (Fail) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
        }
    }
    private sealed class NoSecretChanges : IPageCredentialStore
    {
        public int Writes;
        public bool TryRead(string target, out string? secret) { secret = "unchanged"; return true; }
        public void Write(string target, string secret) => Writes++;
        public void Delete(string target) => throw new InvalidOperationException("Unexpected credential delete");
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tcm_quota_reference_" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
