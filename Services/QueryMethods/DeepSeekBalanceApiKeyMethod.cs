using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// deepseek.balance.api-key：DeepSeek 官方 API（api.deepseek.com）+ 普通 API key → 当前余额。
/// 控制台会话用量是独立的私有兼容方法，不自动带入 API 余额方法（解耦官方 API 与控制台来源）。
/// </summary>
public sealed class DeepSeekBalanceApiKeyMethod : IQueryMethod
{
    private static readonly QueryMethodDescriptor Descriptor = new(
        "deepseek.balance.api-key",
        SourceKind.AllowanceOrBalance,
        CredentialClass.ApiKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
        SourceStability.OfficialStable,
        MethodEnablement.Always,
        DefaultPriority: 40,
        MethodSupport.ImplementationVersion);

    private const string Provider = "deepseek";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public QueryMethodDescriptor Describe() => Descriptor;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        if (CredentialResolver.ProviderOf(page.BaseUrl) != Provider)
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NoReliableUsage,
                "Base URL 不匹配 DeepSeek 官方 API（低置信度提示）",
                evidence: new[] { DetectionEvidence.UrlHint(page.BaseUrl) });

        if (!context.Credentials.HasApiKey)
            return MethodSupport.AuthRequired(Descriptor, "页面未配置 DeepSeek API key",
                DetectionEvidence.Auth("需要 Bearer API key"));

        var (status, reason, _) = await ProbeBalanceAsync(page, ct);
        if (status != CandidateStatus.Available)
            return MethodSupport.NotAvailable(Descriptor, status, reason, evidence: new[] { DetectionEvidence.Field("/user/balance") });

        return MethodSupport.Available(Descriptor, context.Credentials.Scope, Coverage.Unknown,
            new[] { DetectionEvidence.Field("/user/balance.balance_infos[0].total_balance") },
            source: new SourceIdentity(Provider, "api-key", Descriptor.MethodId, $"{page.BaseUrl.TrimEnd('/')}/user/balance"),
            confidence: 90);
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        try
        {
            var (balance, currency) = await FetchBalanceAsync(page, ct);
            if (balance is null)
                return MethodQueryResult.Empty(SnapshotStatus.SchemaMismatch, "余额接口未返回 total_balance");

            var scope = candidate.CredentialScope ?? new CredentialScope(CredentialClass.ApiKey, Provider);
            var source = candidate.Source ?? new SourceIdentity(Provider, "api-key", Descriptor.MethodId, $"{page.BaseUrl.TrimEnd('/')}/user/balance");
            var value = new BalanceQuotaValue(
                CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
                Confidence: 1.0, IsPrivate: false, IsEstimated: false,
                Balance: balance, Used: null, Limit: null, Remaining: null,
                Currency: currency, Unit: null, ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15));
            return new MethodQueryResult(new CapabilityValue[] { value }, SnapshotStatus.Success, null, DateTimeOffset.UtcNow);
        }
        catch (QueryTransportException ex)
        {
            return MethodQueryResult.Empty(QueryFailureClassifier.SnapshotStatusOf(ex.Status), ex.Message)
                with { Failure = new FailureInfo(ex.Status, ex.Message, DateTimeOffset.UtcNow) };
        }
    }

    private async Task<(CandidateStatus Status, string Reason, decimal? Balance)> ProbeBalanceAsync(PageConfigRecord page, CancellationToken ct)
    {
        try
        {
            var (balance, currency) = await FetchBalanceAsync(page, ct);
            _ = currency;
            return balance is null
                ? (CandidateStatus.SchemaMismatch, "余额接口未返回 total_balance", null)
                : (CandidateStatus.Available, "", balance);
        }
        catch (QueryTransportException ex)
        {
            return (ex.Status, ex.Message, null);
        }
    }

    private async Task<(decimal? Balance, string Currency)> FetchBalanceAsync(PageConfigRecord page, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{page.BaseUrl.TrimEnd('/')}/user/balance");
            var key = page.CredentialRef.Target is { } target
                && CredentialStore.TryReadSecret(target, out var k) ? k : null;
            if (!string.IsNullOrEmpty(key))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => CandidateStatus.AuthRequired,
                    System.Net.HttpStatusCode.Forbidden => CandidateStatus.Forbidden,
                    System.Net.HttpStatusCode.TooManyRequests => CandidateStatus.RateLimited,
                    _ when (int)response.StatusCode >= 500 => CandidateStatus.NetworkFailure,
                    _ => CandidateStatus.SchemaMismatch,
                };
                throw new QueryTransportException(status, $"DeepSeek balance HTTP {(int)response.StatusCode}", (int)response.StatusCode);
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("balance_infos", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new QueryTransportException(CandidateStatus.SchemaMismatch, "DeepSeek balance 缺少 balance_infos");
            foreach (var b in arr.EnumerateArray())
            {
                if (!b.TryGetProperty("total_balance", out var tb)) continue;
                var text = tb.ValueKind == JsonValueKind.String ? tb.GetString() : tb.GetRawText();
                if (decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    var currency = b.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString()! : "CNY";
                    return (v, currency);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new QueryTransportException(CandidateStatus.NetworkFailure, "DeepSeek balance 请求超时");
        }
        catch (HttpRequestException ex)
        {
            throw new QueryTransportException(CandidateStatus.NetworkFailure, "DeepSeek balance 网络错误", inner: ex);
        }
        throw new QueryTransportException(CandidateStatus.SchemaMismatch, "DeepSeek balance 未解析到余额");
    }
}
