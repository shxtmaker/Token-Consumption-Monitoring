using System.Net;
using System.Text;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.UI;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class FireworksConsoleBalanceTests
{
    private static PageConfigRecord Page() => new()
    {
        BaseUrl = "https://api.fireworks.ai/inference/v1", Id = "fireworks-test",
        CredentialRef = CredentialReference.SecretKey("fake", CredentialClass.ApiKey),
        EnabledCompatibilityMethods = [FireworksConsoleBalanceMethod.MethodId],
    };

    [Theory]
    [InlineData("$50.23", "50.23")]
    [InlineData("$0.00", "0")]
    [InlineData("$1,234.56", "1234.56")]
    [InlineData("-$0.05", "-0.05")]
    public void ParsesOnlyActualCredits(string credits, string expected)
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { status = "ready", accountId = "account-test", credits }));
        var result = FireworksBalanceWindow.ParseObservation(data.RootElement);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.Balance);
        Assert.Equal("account-test", result.AccountId);
    }

    [Theory]
    [InlineData("$50.23K")]
    [InlineData("$1,23.45")]
    [InlineData("Total Spend: $0.77")]
    [InlineData("")]
    public void RejectsUnknownAmounts(string credits)
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { status = "ready", accountId = "account-test", credits }));
        var error = Assert.Throws<QueryTransportException>(() => FireworksBalanceWindow.ParseObservation(data.RootElement));
        Assert.Equal(CandidateStatus.SchemaMismatch, error.Status);
    }

    [Fact]
    public async Task UsesActualConsoleBalance_WhenApiKeyCanAccessThatAccount()
    {
        var reader = new Reader("account-test", 50.23m);
        var handler = new Stub(_ => Json("""{"accounts":[{"name":"accounts/account-test"}]}"""));
        var method = new FireworksConsoleBalanceMethod(reader, new(handler), _ => "fake-api-key");
        var result = await method.QueryAsync(Page(), null!, default);
        var balance = Assert.IsType<BalanceQuotaValue>(Assert.Single(result.Capabilities));
        Assert.Equal(50.23m, balance.Balance);
        Assert.Equal("USD", balance.Currency);
        Assert.True(balance.IsPrivate);
        Assert.False(balance.IsEstimated);
        Assert.Equal("account-test", balance.Source.Account);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
    }

    [Fact]
    public async Task WrongConsoleAccount_DoesNotLeakAnotherAccountsBalance()
    {
        var method = new FireworksConsoleBalanceMethod(new Reader("wrong", 500m),
            new(new Stub(_ => Json("""{"accounts":[{"name":"accounts/expected"}]}"""))), _ => "fake");
        var result = await method.QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.Forbidden, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task FollowsApiAccountPaginationBeforeRejectingSession()
    {
        var handler = new Stub(r => Json(r.RequestUri!.Query.Contains("pageToken")
            ? """{"accounts":[{"name":"accounts/expected"}]}"""
            : """{"accounts":[],"nextPageToken":"next"}"""));
        var result = await new FireworksConsoleBalanceMethod(new Reader("expected", 10m), new(handler), _ => "fake")
            .QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.Success, result.Status);
    }

    [Fact]
    public async Task MissingSession_IsAuthRequired_NotZeroBalance()
    {
        var result = await new FireworksConsoleBalanceMethod(null, readKey: _ => "fake").QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.AuthRequired, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task DisabledOrUntrustedSource_NeverReadsCredential()
    {
        var method = new FireworksConsoleBalanceMethod(null, readKey: _ => throw new Exception("must not read"));
        var page = Page();
        page.EnabledCompatibilityMethods.Clear();
        Assert.Equal(SnapshotStatus.PermanentFailure, (await method.QueryAsync(page, null!, default)).Status);
        page.EnabledCompatibilityMethods.Add(FireworksConsoleBalanceMethod.MethodId);
        page.BaseUrl = "https://api.fireworks.ai.evil.invalid/inference/v1";
        Assert.Equal(SnapshotStatus.PermanentFailure, (await method.QueryAsync(page, null!, default)).Status);
    }

    [Theory]
    [InlineData("https://app.fireworks.ai/account/home", true)]
    [InlineData("https://app.fireworks.ai.evil.invalid/", false)]
    [InlineData("http://app.fireworks.ai/", false)]
    [InlineData("https://app.fireworks.ai:8443/", false)]
    public void AcceptsMessagesOnlyFromConsoleOrigin(string url, bool accepted)
        => Assert.Equal(accepted, FireworksBalanceWindow.IsConsoleOrigin(url));

    private sealed class Reader(string account, decimal balance) : IFireworksConsoleBalanceReader
    {
        public Task<FireworksConsoleBalance> ReadAsync(PageConfigRecord page, CancellationToken ct)
            => Task.FromResult(new FireworksConsoleBalance(account, balance, DateTimeOffset.UtcNow));
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? AuthorizationScheme;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            Assert.Equal("api.fireworks.ai", request.RequestUri!.Host);
            return Task.FromResult(respond(request));
        }
    }
}
