using System.Text;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class CodexAccountTests
{
    private static PageConfigRecord Page() => new() { BaseUrl = "https://chatgpt.com", CredentialRef = CredentialReference.LocalRecord };
    private static ScanContext Context(PageConfigRecord page) => new()
    { Page = page, ConfigurationFingerprint = "test", Credentials = new(page) };
    private sealed class Client(string json) : ICodexAccountClient
    {
        public bool IsAvailable { get; init; } = true;
        public Exception? Error { get; set; }
        public Action? OnRead { get; init; }
        public string? Method;
        public Task<JsonElement> ReadAsync(string method, CancellationToken ct)
        {
            Method = method;
            OnRead?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Error is { } error ? Task.FromException<JsonElement>(error)
                : Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        }
    }

    [Theory]
    [InlineData(false, "{\"rateLimits\":{\"primary\":{\"usedPercent\":20},\"secondary\":null}}", "account/rateLimits/read")]
    [InlineData(false, "{\"rateLimits\":{\"primary\":null,\"secondary\":null}}", "account/rateLimits/read")]
    [InlineData(true, "{\"summary\":{\"lifetimeTokens\":42}}", "account/usage/read")]
    [InlineData(true, "{\"summary\":{\"lifetimeTokens\":null}}", "account/usage/read")]
    public async Task Scan_VerifiesCorrespondingAccountMethodIncludingSuccessfulEmptyData(bool usage, string json, string expectedMethod)
    {
        var client = new Client(json);
        var page = Page();
        var candidate = await new CodexAccountMethod(usage, client).ScanAsync(page, Context(page), default);
        Assert.Equal(expectedMethod, client.Method);
        Assert.True(candidate.IsAvailable);
        Assert.Null(candidate.Failure);
    }

    [Theory]
    [InlineData(CandidateStatus.Unsupported)]
    [InlineData(CandidateStatus.AuthRequired)]
    [InlineData(CandidateStatus.Forbidden)]
    [InlineData(CandidateStatus.NetworkFailure)]
    [InlineData(CandidateStatus.SchemaMismatch)]
    public async Task Scan_FailedAccountQueryNeverBecomesAvailable(CandidateStatus status)
    {
        var client = new Client("{}") { Error = new QueryTransportException(status, "账户查询未通过") };
        var page = Page();
        var method = new CodexAccountMethod(client: client);
        var candidate = await method.ScanAsync(page, Context(page), default);
        Assert.False(candidate.IsAvailable);
        Assert.Equal(status, candidate.Status);
        Assert.Equal(status, candidate.Failure!.Status);

    }

    [Fact]
    public async Task Rescan_RechecksAccountAfterEarlierSuccess()
    {
        var client = new Client("{\"summary\":{\"lifetimeTokens\":42}} ");
        var page = Page();
        var method = new CodexAccountMethod(usage: true, client: client);
        Assert.True((await method.ScanAsync(page, Context(page), default)).IsAvailable);
        client.Error = new QueryTransportException(CandidateStatus.AuthRequired, "请重新登录");
        Assert.False((await method.ScanAsync(page, Context(page), default)).IsAvailable);
        client.Error = null;
        Assert.True((await method.ScanAsync(page, Context(page), default)).IsAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_WithoutMatchingConfigurationOrCliDoesNotReadAccount(bool missingCli)
    {
        var client = new Client("{}") { IsAvailable = !missingCli };
        var page = Page();
        if (!missingCli) page.CredentialRef = CredentialReference.ApiKeyTarget("unused");
        var candidate = await new CodexAccountMethod(client: client).ScanAsync(page, Context(page), default);
        Assert.Equal(CandidateStatus.Unsupported, candidate.Status);
        Assert.Null(client.Method);
    }

    [Fact]
    public async Task Scan_ParseFailureDoesNotExposeResponseDetails()
    {
        var client = new Client("SECRET ACCOUNT RESPONSE");
        var page = Page();
        var candidate = await new CodexAccountMethod(client: client).ScanAsync(page, Context(page), default);
        Assert.Equal(CandidateStatus.SchemaMismatch, candidate.Status);
        Assert.DoesNotContain("SECRET", candidate.Failure!.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_CallerCancellationIsPropagated(bool duringRead)
    {
        using var cancellation = new CancellationTokenSource();
        if (!duringRead) cancellation.Cancel();
        var client = new Client("{}") { OnRead = duringRead ? cancellation.Cancel : null };
        var page = Page();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexAccountMethod(client: client)
            .ScanAsync(page, Context(page), cancellation.Token));
        Assert.Equal(duringRead ? "account/rateLimits/read" : null, client.Method);
    }

    [Fact]
    public async Task MultiBucketLimits_PreferredOverLegacyAndNeverHardcodePlanWindows()
    {
        var client = new Client("""
        {"accountId":"test-account","rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{
        "codex":{"limitName":"Codex","primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":1900000000},"secondary":null},
        "review":{"primary":null,"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1900000001}}}}
        """);
        var result = await new CodexAccountMethod(client: client).QueryAsync(Page(), null!, default);
        Assert.Equal("account/rateLimits/read", client.Method);
        Assert.Equal(SnapshotStatus.Success, result.Status);
        Assert.Equal(2, result.Capabilities.Count);
        var windows = result.Capabilities.Cast<RollingWindowValue>().ToList();
        Assert.Equal(20, windows[0].Percent);
        Assert.Contains("5 小时", windows[0].WindowName);
        Assert.Contains("7 天", windows[1].WindowName);
        Assert.Equal("test-account", windows[0].Source.Account);
        Assert.Null(windows[0].Limit);
    }

    [Fact]
    public async Task NullLimits_AreUnavailableNotZeroConsumption()
    {
        var client = new Client("""{"rateLimits":{"primary":null,"secondary":null},"rateLimitsByLimitId":null}""");
        var result = await new CodexAccountMethod(client: client).QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.NoData, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task LifetimeTokens_Are64BitAndExplicitlyNotDaily()
    {
        var client = new Client("""{"summary":{"lifetimeTokens":5000000000},"dailyUsageBuckets":null}""");
        var result = await new CodexAccountMethod(usage: true, client: client).QueryAsync(Page(), null!, default);
        var usage = Assert.IsType<ReportedUsageValue>(Assert.Single(result.Capabilities));
        Assert.Equal(5000000000, usage.TotalTokens);
        Assert.Contains("非今日", usage.Coverage.Scope);
        Assert.Equal("account/usage/read", client.Method);
        Assert.Null(usage.TotalRequests);
    }

    [Fact]
    public async Task ExplicitLocalSelectionRequiredBeforeCallingClient()
    {
        var client = new Client("{}");
        var page = Page();
        page.CredentialRef = CredentialReference.ApiKeyTarget("unused");
        var result = await new CodexAccountMethod(client: client).QueryAsync(page, null!, default);
        Assert.Equal(SnapshotStatus.PermanentFailure, result.Status);
        Assert.Null(client.Method);
    }

    [Fact]
    public async Task JsonRpcNotificationsAndOtherIdsAreSkipped()
    {
        const string lines = "{\"method\":\"some/notification\"}\n{\"id\":0,\"result\":{}}\n{\"id\":1,\"result\":{\"ok\":true}}\n";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(lines)));
        Assert.True((await CodexAppServerClient.ReadResponseAsync(reader, 1, default)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task JsonRpcErrors_DoNotExposeBackendMessages()
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1,\"error\":{\"code\":-32601,\"message\":\"secret account details\"}}\n")));
        var error = await Assert.ThrowsAsync<QueryTransportException>(() => CodexAppServerClient.ReadResponseAsync(reader, 1, default));
        Assert.Equal(CandidateStatus.Unsupported, error.Status);
        Assert.DoesNotContain("secret", error.Message);
    }
}
