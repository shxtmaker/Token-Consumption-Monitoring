using System.Text.Json;
using System.Windows.Threading;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class OpenDesignTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T00:00:00Z");
    private static PageConfigRecord Page(string workspace = "workspace-a") => new()
    {
        Id = "test-page", Name = "OpenDesign", BaseUrl = OpenDesignConsoleMethod.DashboardUrl(workspace),
        CredentialRef = CredentialReference.GlobalConsoleSession("test-console"),
        EnabledCompatibilityMethods = [OpenDesignConsoleMethod.BalanceMethodId, OpenDesignConsoleMethod.WindowsMethodId],
    };
    private static JsonElement Observation(string body, string workspace = "workspace-a") => JsonSerializer.SerializeToElement(new
    {
        status = "ready", context = new { workspaceId = workspace, workspaceMemberId = "member-a" },
        body = JsonSerializer.Deserialize<JsonElement>(body),
    });
    private static JsonElement Windows() => Observation("""
        {"eligible":true,"tier":"go","windows":[
          {"policyId":"5h","durationSeconds":18000,"limitCredits":"10000","remainingCredits":"9996.6","resetsAt":"2026-10-13T03:00:00Z"},
          {"policyId":"7d","durationSeconds":604800,"limitCredits":"100000","remainingCredits":"96728","resetsAt":"2026-10-17T10:00:00Z"}
        ]}
        """);

    [Theory]
    [InlineData("https://open-design.ai/cloud/dashboard?workspaceId=abc_123-DEF", true)]
    [InlineData("https://open-design.ai/cloud/dashboard/?workspaceId=abc", true)]
    [InlineData("https://open-design.ai/cloud/dashboard", false)]
    [InlineData("https://open-design.ai.evil.invalid/cloud/dashboard?workspaceId=abc", false)]
    [InlineData("https://user@open-design.ai/cloud/dashboard?workspaceId=abc", false)]
    [InlineData("http://open-design.ai/cloud/dashboard?workspaceId=abc", false)]
    [InlineData("https://open-design.ai:8443/cloud/dashboard?workspaceId=abc", false)]
    [InlineData("https://open-design.ai/cloud/dashboard?workspaceId=a&workspaceId=b", false)]
    [InlineData("https://open-design.ai/cloud/dashboard?workspaceId=a&billing=checkout", false)]
    [InlineData("https://open-design.ai/cloud/dashboard?workspaceId=%2Fother", false)]
    [InlineData("https://open-design.ai/cloud/dashboard?workspaceId=a#fragment", false)]
    public void AcceptsOnlyExplicitWorkspaceLinks(string url, bool accepted)
        => Assert.Equal(accepted, OpenDesignConsoleMethod.TryGetWorkspace(url, out _));

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1.50", "1.50")]
    [InlineData("-0.002", "-0.002")]
    public void BalanceUsesReportedUsdWithoutInferringTokenUsage(string amount, string expected)
    {
        var result = OpenDesignConsoleMethod.Parse(Observation($$"""{"balanceUsd":"{{amount}}"}"""), "page", "workspace-a", false, Now);
        var value = Assert.IsType<BalanceQuotaValue>(Assert.Single(result.Capabilities));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value.Balance);
        Assert.Null(value.Used);
        Assert.Null(value.Limit);
        Assert.Equal("USD", value.Currency);
        Assert.True(value.IsPrivate);
        Assert.False(value.IsEstimated);
        Assert.Equal("workspace-a", value.CredentialScope.Team);
        Assert.Equal("member-a", value.CredentialScope.Account);
    }

    [Fact]
    public void WindowsUseConsumedPercentAndPreserveBothPoliciesAndResetTimes()
    {
        var result = OpenDesignConsoleMethod.Parse(Windows(), "page", "workspace-a", true, Now);
        Assert.Equal(2, result.Capabilities.Count);
        var shortWindow = Assert.IsType<RollingWindowValue>(result.Capabilities[0]);
        var week = Assert.IsType<RollingWindowValue>(result.Capabilities[1]);
        Assert.Equal(0, shortWindow.Percent);
        Assert.Equal(3, week.Percent);
        Assert.Null(week.Used);
        Assert.Equal("5 小时额度", shortWindow.WindowName);
        Assert.Equal("7 天额度", week.WindowName);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T10:00:00Z"), week.ResetsAt);
        Assert.Null(shortWindow.Remaining); // 不把小数 credits 截断或标成 tokens。
        Assert.Equal("percent", week.Unit);
        Assert.Null(week.Limit);
        Assert.Null(week.Remaining);
        Assert.NotEqual(CapabilityItemKey.For(shortWindow), CapabilityItemKey.For(week));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"balanceUsd\":null}")]
    [InlineData("{\"balanceUsd\":\"NaN\"}")]
    [InlineData("{\"balanceUsd\":\"1.50 USD\"}")]
    public async Task MissingOrMalformedBalanceIsNeverZero(string body)
    {
        var result = await new OpenDesignConsoleMethod(new Reader(Observation(body))).QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.SchemaMismatch, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Theory]
    [InlineData("auth", SnapshotStatus.AuthRequired)]
    [InlineData("forbidden", SnapshotStatus.Forbidden)]
    [InlineData("rate", SnapshotStatus.RateLimited)]
    [InlineData("network", SnapshotStatus.TemporaryFailure)]
    [InlineData("schema", SnapshotStatus.SchemaMismatch)]
    public async Task FailureStatesRemainExplicit(string status, SnapshotStatus expected)
    {
        var result = await new OpenDesignConsoleMethod(new Reader(JsonSerializer.SerializeToElement(new { status })))
            .QueryAsync(Page(), null!, default);
        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Capabilities);
        if (status == "rate") Assert.NotNull(result.Failure!.RetryAt);
    }

    [Fact]
    public async Task MismatchedWorkspaceCannotPublishAnotherWorkspacesBalance()
    {
        var method = new OpenDesignConsoleMethod(new Reader(Observation("{\"balanceUsd\":\"999\"}", "workspace-b")));
        Assert.Equal(SnapshotStatus.Forbidden, (await method.QueryAsync(Page(), null!, default)).Status);
        method = new(new Reader(Observation("{\"balanceUsd\":\"999\",\"workspaceId\":\"workspace-b\"}")));
        Assert.Equal(SnapshotStatus.Forbidden, (await method.QueryAsync(Page(), null!, default)).Status);
    }

    [Fact]
    public async Task NoPlanDoesNotHideWorkingWallet()
    {
        var reader = new Reader(Observation("{\"eligible\":false,\"windows\":[]}"));
        var noPlan = await new OpenDesignConsoleMethod(reader, true).QueryAsync(Page(), null!, default);
        Assert.Equal(CandidateStatus.NoReliableUsage, noPlan.Failure!.Status);
        Assert.Empty(noPlan.Capabilities);
        var wallet = await new OpenDesignConsoleMethod(new Reader(Observation("{\"balanceUsd\":\"1.50\"}"))).QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.Success, wallet.Status);
    }

    [Theory]
    [InlineData("0", "0", "null")]
    [InlineData("-100", "0", "null")]
    [InlineData("100", "NaN", "null")]
    [InlineData("100", "5", "\"bad-date\"")]
    public async Task InvalidWindowIsSchemaMismatch(string limit, string remaining, string reset)
    {
        var body = $$"""{"eligible":true,"windows":[{"policyId":"week","durationSeconds":604800,"limitCredits":"{{limit}}","remainingCredits":"{{remaining}}","resetsAt":{{reset}}}]}""";
        var result = await new OpenDesignConsoleMethod(new Reader(Observation(body)), true).QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.SchemaMismatch, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Theory]
    [InlineData("-1", 100)]
    [InlineData("0", 100)]
    [InlineData("101", 0)]
    [InlineData("12.5", 88)]
    public void ExhaustedOrBonusWindowMatchesConsoleClamping(string remaining, int percent)
    {
        var body = $$"""{"eligible":true,"windows":[{"policyId":"week","durationSeconds":604800,"limitCredits":"100","remainingCredits":"{{remaining}}"}]}""";
        var value = Assert.IsType<RollingWindowValue>(Assert.Single(OpenDesignConsoleMethod.Parse(Observation(body), "page", "workspace-a", true, Now).Capabilities));
        Assert.Equal(percent, value.Percent);
        Assert.Null(value.ResetsAt);
    }

    [Fact]
    public async Task DisabledAndUntrustedMethodsNeverReadSession()
    {
        var reader = new Reader(Observation("{\"balanceUsd\":\"1\"}"));
        var page = Page();
        page.EnabledCompatibilityMethods.Clear();
        await new OpenDesignConsoleMethod(reader).QueryAsync(page, null!, default);
        page = Page(); page.BaseUrl = "https://other.invalid/";
        await new OpenDesignConsoleMethod(reader).QueryAsync(page, null!, default);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task MissingSessionIsAuthRequired()
        => Assert.Equal(SnapshotStatus.AuthRequired, (await new OpenDesignConsoleMethod(null).QueryAsync(Page(), null!, default)).Status);

    [Fact]
    public void ChangingOnlyWorkspaceInvalidatesFingerprintAndBrowserProfile()
    {
        var first = Page(); var other = Page("workspace-b");
        var fingerprints = new FingerprintBuilder(new[] { new OpenDesignConsoleMethod(null).Describe() });
        Assert.NotEqual(fingerprints.Build(first), fingerprints.Build(other));
        Assert.NotEqual(OpenDesignConsoleSession.SessionKey(first), OpenDesignConsoleSession.SessionKey(other));
        other = Page(); other.Id = "second-page";
        Assert.NotEqual(OpenDesignConsoleSession.SessionKey(first), OpenDesignConsoleSession.SessionKey(other));
    }

    [Fact]
    public async Task OpenDesignSessionIsNeverADeepSeekSourceOrModelProbe()
    {
        using var session = new DeepSeekSessionService(Dispatcher.CurrentDispatcher);
        var method = new DeepSeekConsoleUsageOnlineMethod(session, new DeepSeekUsageClient(session));
        var page = Page();
        var context = new ScanContext { Page = page, ConfigurationFingerprint = "test", Credentials = new(page) };
        Assert.Equal(CandidateStatus.Unsupported, (await method.ScanAsync(page, context, default)).Status);
        Assert.Empty((await method.QueryAsync(page, null!, default)).Capabilities);
        Assert.Equal(CandidateStatus.Unsupported, (await new EndpointProbeMethod().ScanAsync(page, context, default)).Status);
    }

    [Fact]
    public void EditorSavesAndReloadsConsolePageWithoutReadingOrWritingSecret()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_opendesign_config_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PageConfigStore(directory);
            var catalog = new PageCatalog(store.Load().Document.Pages);
            var commands = new PageConfigurationCommands(catalog, store, new NoSecrets());
            var editor = new PageEditorViewModel
            {
                Name = "OpenDesign", BaseUrl = Page().BaseUrl, KeyClass = CredentialClass.ConsoleSession,
                Protocol = KeyFormat.Protocol.DeepSeekConsole, EnableCompatibility = false,
            };
            Assert.False(editor.Save(commands, "").Succeeded);
            editor.EnableCompatibility = true;
            Assert.True(editor.Save(commands, "").Succeeded);
            var saved = new PageConfigStore(directory).Load().Document.Pages.Single();
            Assert.Equal(CredentialClass.ConsoleSession, saved.CredentialRef.ResolveClass());
            Assert.Equal(KeyFormat.Protocol.ChatCompletions, saved.ParseProtocol());
            Assert.Contains(OpenDesignConsoleMethod.BalanceMethodId, saved.EnabledCompatibilityMethods);
            Assert.Contains(OpenDesignConsoleMethod.WindowsMethodId, saved.EnabledCompatibilityMethods);
            Assert.Equal("", commands.ReadApiKeyForEditing(saved));
            editor.Editing = catalog.Snapshot().Single();
            editor.BaseUrl = Page("workspace-b").BaseUrl;
            Assert.True(editor.Save(commands, "").Succeeded);
            Assert.Equal(saved.Id, catalog.Snapshot().Single().Id);
            Assert.Equal(Page("workspace-b").BaseUrl, new PageConfigStore(directory).Load().Document.Pages.Single().BaseUrl);
            editor.BaseUrl = "https://example.invalid";
            Assert.False(editor.Save(commands, "").Succeeded);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DefaultRegistryPublishesBothCapabilitiesAndKeepsWorkspaceFailureIsolated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_opendesign_runtime_" + Guid.NewGuid().ToString("N"));
        using var session = new DeepSeekSessionService(Dispatcher.CurrentDispatcher);
        try
        {
            var reader = new MutableReader();
            var registry = QueryMethodRegistry.BuildDefault(new OpenCodeUsageClient(), new OpenCodeAuthService(new OAuthDeviceFlowClient()),
                session, new DeepSeekUsageClient(session), new CommandCodeUsageClient(), openDesign: reader);
            var coordinator = new PageRuntimeCoordinator(registry, new FingerprintBuilder(registry.Descriptors),
                new MethodStateStore(directory), new MethodResultCache());
            var page = Page();
            // 与编辑器勾选兼容查询后的配置一致，确认其他供应商不会混入。
            page.EnabledCompatibilityMethods.AddRange(["opencode.rolling-window.api-key", "opencode.allowance.oauth",
                "commandcode.allowance-window.compat", "deepseek.console-usage.compat", "fireworks.balance.console"]);
            var first = await coordinator.RefreshAsync(page, RefreshReason.Manual, default);
            Assert.Equal(SnapshotStatus.Success, first.Snapshot.Status);
            Assert.Single(first.Snapshot.Balances);
            Assert.Equal(2, first.Snapshot.Windows.Count());
            Assert.Equal(2, first.Snapshot.Metadata.EffectiveSelectedMethodIds.Count);
            Assert.All(first.Snapshot.Capabilities, c => Assert.Equal("opendesign", c.Source.Provider));
            var projection = new MonitorState();
            projection.ApplySnapshot(first.Snapshot, true);
            Assert.Equal("余额 1.5 USD", projection.Snapshot.BalanceLabel);
            Assert.All(projection.Snapshot.Windows, w => Assert.Equal("", w.AbsoluteLabel));
            Assert.False(projection.Snapshot.HasReportedUsage);

            reader.WindowFailure = "network";
            var partial = await coordinator.RefreshAsync(page, RefreshReason.Manual, default);
            Assert.Equal(SnapshotStatus.SuccessPartial, partial.Snapshot.Status);
            Assert.All(partial.Snapshot.Balances, b => Assert.False(b.IsStale));
            Assert.All(partial.Snapshot.Windows, w => Assert.True(w.IsStale));

            reader.AllFailure = "auth";
            page.BaseUrl = Page("workspace-b").BaseUrl;
            var switched = await coordinator.RefreshAsync(page, RefreshReason.Manual, default);
            Assert.Equal(SnapshotStatus.AuthRequired, switched.Snapshot.Status);
            Assert.Empty(switched.Snapshot.Capabilities);
            Assert.Equal(CredentialClass.ConsoleSession, switched.AuthCredentialClass);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutPublishingFailureOrZero()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var method = new OpenDesignConsoleMethod(new CancelledReader());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => method.QueryAsync(Page(), null!, cancelled.Token));
    }

    private sealed class CancelledReader : IOpenDesignConsoleReader
    {
        public Task<JsonElement> ReadAsync(PageConfigRecord page, bool windows, CancellationToken ct)
            => Task.FromCanceled<JsonElement>(ct);
    }
    private sealed class MutableReader : IOpenDesignConsoleReader
    {
        public string? WindowFailure;
        public string? AllFailure;
        public Task<JsonElement> ReadAsync(PageConfigRecord page, bool windows, CancellationToken ct)
        {
            var failure = AllFailure ?? (windows ? WindowFailure : null);
            return Task.FromResult(failure is not null ? JsonSerializer.SerializeToElement(new { status = failure })
                : windows ? Windows() : Observation("{\"balanceUsd\":\"1.50\"}"));
        }
    }
    private sealed class NoSecrets : IPageCredentialStore
    {
        public bool TryRead(string target, out string? secret) => throw new InvalidOperationException("Must not read credentials");
        public void Write(string target, string secret) => throw new InvalidOperationException("Must not write credentials");
        public void Delete(string target) => throw new InvalidOperationException("Must not delete credentials");
    }

    private sealed class Reader(JsonElement response) : IOpenDesignConsoleReader
    {
        public int Calls { get; private set; }
        public Task<JsonElement> ReadAsync(PageConfigRecord page, bool windows, CancellationToken ct)
        { Calls++; return Task.FromResult(response); }
    }
}
