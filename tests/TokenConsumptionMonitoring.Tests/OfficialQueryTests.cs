using System.Net;
using System.Text;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI.Diagnostics;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class OfficialQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Start = Now.Date.AddDays(-1).Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
    private static readonly long End = Now.Date.Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
    private static PageConfigRecord Page(string url, CredentialClass kind = CredentialClass.ApiKey) => new()
    {
        Id = "test-page", Name = "test", BaseUrl = url,
        CredentialRef = CredentialReference.SecretKey("test-only", kind),
    };
    private static string? Secret(PageConfigRecord page, CredentialClass kind) => "test-secret";
    private static ScanContext Context(PageConfigRecord page) => new()
    { Page = page, ConfigurationFingerprint = "test", Credentials = new(page) };
    private static string OpenAi(string rows, bool more = false, string? next = null)
        => $$"""{"data":[{"start_time":{{Start}},"end_time":{{End}},"results":{{rows}}}],"has_more":{{more.ToString().ToLowerInvariant()}},"next_page":{{JsonSerializer.Serialize(next)}}} """;
    private static string Claude(string rows)
        => $$"""{"data":[{"starting_at":"2026-09-07T00:00:00Z","ending_at":"2026-09-08T00:00:00Z","results":{{rows}}}],"has_more":false,"next_page":null} """;

    [Theory]
    [InlineData("http://api.deepseek.com")]
    [InlineData("https://api.deepseek.com.evil.invalid")]
    [InlineData("https://evil.invalid/deepseek.com")]
    [InlineData("https://api.deepseek.com:8443")]
    [InlineData("https://someone@api.deepseek.com")]
    [InlineData("https://api.deepseek.com/path")]
    [InlineData("https://api.deepseek.com?secret=value")]
    public async Task UntrustedEndpoint_NeverReadsOrSendsCredential(string url)
    {
        var handler = new Stub(_ => throw new Exception("must not send"));
        var reads = 0;
        var method = new DeepSeekBalanceApiKeyMethod(new(handler), (p, k) => { reads++; return "secret"; });
        var page = Page(url);
        Assert.Equal(CandidateStatus.Unsupported, (await method.ScanAsync(page, Context(page), default)).Status);
        Assert.Equal(SnapshotStatus.PermanentFailure, (await method.QueryAsync(page, null!, default)).Status);
        Assert.Equal(0, reads);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DeepSeek_PreservesAllCurrenciesAndVersionedBasePath()
    {
        var handler = new Stub(request =>
        {
            Assert.Equal("https://api.deepseek.com/user/balance", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Json("""{"balance_infos":[{"currency":"CNY","total_balance":"0"},{"currency":"USD","total_balance":"-0.25"}]}""");
        });
        var method = new DeepSeekBalanceApiKeyMethod(new(handler), Secret);
        var result = await method.QueryAsync(Page("https://api.deepseek.com/v1/"), null!, default);
        Assert.Equal(SnapshotStatus.Success, result.Status);
        Assert.Collection(result.Capabilities.Cast<BalanceQuotaValue>(),
            x => { Assert.Equal("CNY", x.Currency); Assert.Equal(0m, x.Balance); },
            x => { Assert.Equal("USD", x.Currency); Assert.Equal(-0.25m, x.Balance); });
    }

    [Theory]
    [InlineData("api.moonshot.cn", "CNY")]
    [InlineData("api.moonshot.ai", "USD")]
    public async Task Moonshot_UsesReportedAvailableBalanceAndRegionCurrency(string host, string currency)
    {
        var method = new MoonshotBalanceMethod(new(new Stub(_ => Json("""{"code":0,"status":true,"data":{"available_balance":8.5,"cash_balance":-1.5,"voucher_balance":10}}"""))), Secret);
        var balance = Assert.IsType<BalanceQuotaValue>(Assert.Single((await method.QueryAsync(Page($"https://{host}/v1"), null!, default)).Capabilities));
        Assert.Equal(8.5m, balance.Balance);
        Assert.Equal(currency, balance.Currency);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"balance_infos\":[{\"total_balance\":0}]}")]
    [InlineData("{\"balance_infos\":[{\"currency\":\"USD\",\"total_balance\":\"NaN\"}]}")]
    [InlineData("{\"balance_infos\":[{\"currency\":\"USD\",\"total_balance\":1},{\"currency\":\"USD\",\"total_balance\":2}]}")]
    public async Task MissingOrMalformedBalance_IsNotZero(string body)
    {
        var method = new DeepSeekBalanceApiKeyMethod(new(new Stub(_ => Json(body))), Secret);
        var result = await method.QueryAsync(Page("https://api.deepseek.com"), null!, default);
        Assert.Equal(SnapshotStatus.SchemaMismatch, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task OpenRouter_UnlimitedIsNoData_NotZeroBalance()
    {
        var method = new OpenRouterKeyMethod(http: new(new Stub(_ => Json("""{"data":{"limit":null,"limit_remaining":null,"usage":1000}}"""))), readSecret: Secret);
        Assert.Equal(SnapshotStatus.NoData, (await method.QueryAsync(Page("https://openrouter.ai/api/v1"), null!, default)).Status);
    }

    [Fact]
    public async Task OpenRouter_LifetimeUsageNeverBecomesMonthlyUsedQuota()
    {
        var method = new OpenRouterKeyMethod(http: new(new Stub(_ => Json("""{"data":{"limit":20,"limit_remaining":15,"limit_reset":"monthly","usage":10000,"usage_monthly":200}}"""))), readSecret: Secret);
        var value = Assert.IsType<BalanceQuotaValue>(Assert.Single((await method.QueryAsync(Page("https://openrouter.ai"), null!, default)).Capabilities));
        Assert.Equal(5m, value.Used);
        Assert.Null(value.Balance);
        Assert.Contains("每月", value.Coverage.Scope);
    }

    [Theory]
    [InlineData(401, SnapshotStatus.AuthRequired)]
    [InlineData(403, SnapshotStatus.Forbidden)]
    [InlineData(429, SnapshotStatus.RateLimited)]
    [InlineData(503, SnapshotStatus.TemporaryFailure)]
    [InlineData(302, SnapshotStatus.SchemaMismatch)]
    public async Task HttpFailures_AreClassifiedWithoutEchoingSecrets(int code, SnapshotStatus expected)
    {
        var handler = new Stub(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("SECRET RESPONSE") };
            if (code == 429) response.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
            return response;
        });
        var result = await new DeepSeekBalanceApiKeyMethod(new(handler), Secret).QueryAsync(Page("https://api.deepseek.com"), null!, default);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain("SECRET", result.Failure!.Reason);
        if (code == 429) Assert.True(result.Failure.RetryAt > DateTimeOffset.UtcNow.AddSeconds(90));
    }

    [Fact]
    public async Task ManagementCredential_CannotRunAnOrdinaryKeyMethodOrProbe()
    {
        var page = Page("https://openrouter.ai", CredentialClass.ManagementKey);
        var reads = 0;
        var method = new OpenRouterKeyMethod(readSecret: (p, k) => { reads++; return "secret"; });
        Assert.Equal(CandidateStatus.AuthRequired, (await method.ScanAsync(page, Context(page), default)).Status);
        Assert.Equal(SnapshotStatus.AuthRequired, (await method.QueryAsync(page, null!, default)).Status);
        Assert.Equal(CandidateStatus.AuthRequired, (await new EndpointProbeMethod().ScanAsync(page, Context(page), default)).Status);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task OrganizationUsage_PaginatesAndDoesNotDoubleCountCachedTokens()
    {
        var calls = 0;
        var handler = new Stub(request =>
        {
            Assert.Contains("group_by%5B%5D=model", request.RequestUri!.OriginalString);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            calls++;
            return Json(calls == 1
                ? OpenAi("""[{"model":"test","input_tokens":100,"input_cached_tokens":80,"output_tokens":10,"num_model_requests":1}]""", true, "next value")
                : OpenAi("""[{"model":"test","input_tokens":20,"input_cached_tokens":5,"output_tokens":2,"num_model_requests":1}]"""));
        });
        var method = new OrganizationUsageMethod(false, false, new(handler), Secret, () => Now);
        var value = Assert.IsType<ReportedUsageValue>(Assert.Single((await method.QueryAsync(Page("https://api.openai.com/v1", CredentialClass.AdminKey), null!, default)).Capabilities));
        Assert.Equal(132, value.TotalTokens);
        Assert.Equal(2, value.TotalRequests);
        Assert.Equal(85, value.Models.Single().Breakdown!.CacheHitTokens);
        Assert.Equal(Now.Date.AddDays(-1), value.Coverage.Start!.Value.UtcDateTime);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OrganizationPagingFailure_DiscardsIncompleteTotal()
    {
        var calls = 0;
        var handler = new Stub(_ => ++calls == 1
            ? Json(OpenAi("""[{"amount":{"value":4,"currency":"usd"}}]""", true, "next"))
            : new(HttpStatusCode.Forbidden));
        var result = await new OrganizationUsageMethod(false, true, new(handler), Secret, () => Now)
            .QueryAsync(Page("https://api.openai.com", CredentialClass.AdminKey), null!, default);
        Assert.Equal(SnapshotStatus.Forbidden, result.Status);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task RepeatedCursor_StopsInsteadOfDoubleCountingForever()
    {
        var handler = new Stub(_ => Json(OpenAi("[]", true, "same")));
        var result = await new OrganizationUsageMethod(false, true, new(handler), Secret, () => Now)
            .QueryAsync(Page("https://api.openai.com", CredentialClass.AdminKey), null!, default);
        Assert.Equal(SnapshotStatus.SchemaMismatch, result.Status);
        Assert.Equal(2, handler.Calls);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task AnthropicCost_ConvertsFractionalCentsAndUsesAdminHeaders()
    {
        var handler = new Stub(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.True(request.Headers.Contains("x-api-key"));
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            return Json(Claude("""[{"amount":"123.78912","currency":"USD"}]"""));
        });
        var value = Assert.IsType<ReportedCostValue>(Assert.Single((await new OrganizationUsageMethod(true, true, new(handler), Secret, () => Now)
            .QueryAsync(Page("https://api.anthropic.com", CredentialClass.AdminKey), null!, default)).Capabilities));
        Assert.Equal(1.2378912m, value.Amount);
    }

    [Fact]
    public async Task AnthropicUsage_SumsDistinctCacheClassesButKeepsUnknownRequestCountNull()
    {
        var handler = new Stub(_ => Json(Claude("""[{"uncached_input_tokens":10,"cache_read_input_tokens":20,"cache_creation":{"ephemeral_1h_input_tokens":30,"ephemeral_5m_input_tokens":40},"output_tokens":5}]""")));
        var value = Assert.IsType<ReportedUsageValue>(Assert.Single((await new OrganizationUsageMethod(true, false, new(handler), Secret, () => Now)
            .QueryAsync(Page("https://api.anthropic.com", CredentialClass.AdminKey), null!, default)).Capabilities));
        Assert.Equal(105, value.TotalTokens);
        Assert.Null(value.TotalRequests);
    }

    [Fact]
    public async Task Zai_RawAuthorizationAndOnlyKnownWindows()
    {
        var handler = new Stub(request =>
        {
            Assert.Equal("test-secret", request.Headers.GetValues("Authorization").Single());
            return Json("""{"data":{"limits":[{"type":"TOKENS_LIMIT","percentage":25},{"type":"TIME_LIMIT","percentage":10},{"type":"FUTURE_LIMIT","percentage":99}]}}""");
        });
        var result = await new ZaiCodingPlanMethod(new(handler), Secret).QueryAsync(Page("https://api.z.ai/api/anthropic"), null!, default);
        Assert.Equal(2, result.Capabilities.Count);
        Assert.All(result.Capabilities.Cast<RollingWindowValue>(), w => { Assert.Null(w.ResetsAt); Assert.Null(w.Used); });
        Assert.Equal(25, ((RollingWindowValue)result.Capabilities[0]).Percent);
    }

    [Fact]
    public async Task MiniMax_ExplicitPercentWinsOverAmbiguousUsageCountAndKeepsMilliseconds()
    {
        var handler = new Stub(_ => Json("""{"base_resp":{"status_code":0},"model_remains":[{"model_name":"general","current_interval_remaining_percent":80,"current_interval_usage_count":900,"current_interval_total_count":1000,"start_time":1788825600000,"end_time":1788843600000,"current_weekly_remaining_percent":50,"weekly_boost_permille":1500,"weekly_start_time":1788739200000,"weekly_end_time":1789344000000}]}"""));
        var result = await new MiniMaxTokenPlanMethod(new(handler), Secret).QueryAsync(Page("https://www.minimaxi.com"), null!, default);
        Assert.Equal(SnapshotStatus.Success, result.Status);
        var windows = result.Capabilities.Cast<RollingWindowValue>().ToArray();
        Assert.Equal(20, windows[0].Percent);
        Assert.Null(windows[0].Used);
        Assert.Equal(1788843600000, windows[0].ResetsAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(50, windows[1].Percent);
        Assert.Contains("1.5", windows[1].Status);
    }

    [Fact]
    public async Task MiniMax_AmbiguousLegacyCountsDoNotBecomeInventedConsumption()
    {
        var handler = new Stub(_ => Json("""{"base_resp":{"status_code":0},"model_remains":[{"model_name":"general","current_interval_total_count":1000,"current_interval_usage_count":200}]}"""));
        Assert.Equal(SnapshotStatus.SchemaMismatch, (await new MiniMaxTokenPlanMethod(new(handler), Secret)
            .QueryAsync(Page("https://www.minimaxi.com"), null!, default)).Status);
    }

    [Fact]
    public async Task OfficialMethods_ScanSelectAndProjectUsageAndCostInSeparateSlots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_official_runtime_" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Stub(request => Json(request.RequestUri!.AbsolutePath.EndsWith("/costs")
                ? OpenAi("[{\"amount\":{\"value\":0.12,\"currency\":\"usd\"}}]")
                : OpenAi("[{\"model\":\"test\",\"input_tokens\":100,\"output_tokens\":20,\"num_model_requests\":1}]")));
            var registry = new QueryMethodRegistry(new IQueryMethod[]
            {
                new OrganizationUsageMethod(false, false, new(handler), Secret, () => Now),
                new OrganizationUsageMethod(false, true, new(handler), Secret, () => Now),
            });
            var coordinator = new TokenConsumptionMonitoring.Services.Runtime.PageRuntimeCoordinator(registry,
                new FingerprintBuilder(registry.Descriptors), new TokenConsumptionMonitoring.Services.Persistence.MethodStateStore(directory),
                new TokenConsumptionMonitoring.Services.Runtime.MethodResultCache());
            var result = await coordinator.RefreshAsync(Page("https://api.openai.com", CredentialClass.AdminKey), RefreshReason.Manual, default);
            Assert.Equal(SnapshotStatus.Success, result.Snapshot.Status);
            Assert.Single(result.Snapshot.ReportedUsages);
            Assert.Single(result.Snapshot.ReportedCosts);
            var projection = new CapabilitySnapshotViewModel();
            projection.Update(result.Snapshot, true);
            Assert.Equal("120", projection.ReportedUsageLabel);
            Assert.Contains("UTC 昨日", projection.UsagePeriodLabel);
            Assert.Contains("USD", projection.ReportedCostRows.Single().Label);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response(request)); }
    }
}
