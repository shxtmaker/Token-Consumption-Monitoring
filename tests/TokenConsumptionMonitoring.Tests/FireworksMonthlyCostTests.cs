using System.Net;
using System.Text;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class FireworksMonthlyCostTests
{
    private static PageConfigRecord Page(string url = "https://api.fireworks.ai/inference/v1/") => new()
    { BaseUrl = url, CredentialRef = CredentialReference.SecretKey("test-only", CredentialClass.ApiKey) };

    [Fact]
    public async Task UsesMonthlySpendUsage_NotBudgetOrMaximum_AndFollowsAccountPagination()
    {
        var paths = new List<string>();
        var handler = new Stub(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            paths.Add(request.RequestUri!.PathAndQuery);
            return Json(request.RequestUri.AbsolutePath.EndsWith("monthly-spend-usd")
                ? """{"usage":0.77,"value":"9999999","maxValue":"9999999"}"""
                : request.RequestUri.Query.Contains("pageToken")
                    ? """{"accounts":[{"name":"accounts/second"}]}"""
                    : """{"accounts":[{"name":"accounts/first"}],"nextPageToken":"next"}""");
        });
        var result = await new FireworksMonthlyCostMethod(new(handler), (_, _) => "fake").QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.Success, result.Status);
        Assert.Equal(2, result.Capabilities.Count);
        Assert.All(result.Capabilities, value =>
        {
            var cost = Assert.IsType<ReportedCostValue>(value);
            Assert.Equal(0.77m, cost.Amount);
            Assert.Equal("USD", cost.Currency);
            Assert.False(cost.IsEstimated);
        });
        Assert.NotEqual(result.Capabilities[0].Source, result.Capabilities[1].Source);
        Assert.NotEqual(result.Capabilities[0].Source.StableKey, result.Capabilities[1].Source.StableKey);
        Assert.Contains(paths, p => p.Contains("pageToken=next"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"usage\":null}")]
    [InlineData("{\"usage\":-1}")]
    public async Task MissingOrInvalidUsage_IsUnknown_NotZero(string quota)
    {
        var handler = new Stub(r => Json(r.RequestUri!.AbsolutePath.EndsWith("monthly-spend-usd")
            ? quota : """{"accounts":[{"name":"accounts/test"}]}"""));
        var result = await new FireworksMonthlyCostMethod(new(handler), (_, _) => "fake").QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.SchemaMismatch, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task Forbidden_DoesNotBecomeZeroCost()
    {
        var result = await new FireworksMonthlyCostMethod(new(new Stub(_ => new(HttpStatusCode.Forbidden))), (_, _) => "fake")
            .QueryAsync(Page(), null!, default);
        Assert.Equal(SnapshotStatus.Forbidden, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task UntrustedEndpoint_DoesNotReadCredential()
    {
        var result = await new FireworksMonthlyCostMethod(readSecret: (_, _) => throw new Exception("must not read"))
            .QueryAsync(Page("https://api.fireworks.ai.evil.invalid/inference/v1"), null!, default);
        Assert.Equal(SnapshotStatus.PermanentFailure, result.Status);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}

