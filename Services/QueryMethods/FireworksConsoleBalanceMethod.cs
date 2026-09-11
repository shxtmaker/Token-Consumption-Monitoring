using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

public sealed record FireworksConsoleBalance(string AccountId, decimal Balance, DateTimeOffset FetchedAt);

public interface IFireworksConsoleBalanceReader
{
    Task<FireworksConsoleBalance> ReadAsync(PageConfigRecord page, CancellationToken ct);
}

/// <summary>控制台登录余额；API Key 验证账户归属，浏览器会话独立保存在本机。</summary>
public sealed class FireworksConsoleBalanceMethod(IFireworksConsoleBalanceReader? reader,
    HttpClient? http = null, Func<PageConfigRecord, string?>? readKey = null) : IQueryMethod
{
    public const string MethodId = "fireworks.balance.console";
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private static readonly QueryMethodDescriptor Descriptor = new(MethodId, SourceKind.ConsoleOrPrivateUI,
        CredentialClass.ConsoleSession, QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
        SourceStability.PrivateCompat, MethodEnablement.PrivateCompatOnly, 30, "1.0.0");

    public static bool Matches(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.IdnHost.Equals("api.fireworks.ai", StringComparison.OrdinalIgnoreCase)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && new[] { "", "/v1", "/inference/v1" }.Contains(uri.AbsolutePath.TrimEnd('/'));

    public QueryMethodDescriptor Describe() => Descriptor;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        var result = await QueryAsync(page, null!, ct);
        if (result.Status != SnapshotStatus.Success)
            return MethodSupport.NotAvailable(Descriptor, result.Failure!.Status, result.Failure.Reason);
        var balance = result.Capabilities[0];
        return MethodSupport.Available(Descriptor, balance.CredentialScope, balance.Coverage,
            [DetectionEvidence.Field("控制台 Credits 余额，账户已与 API Key 核对")], balance.Source, 90);
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        try
        {
            if (!Matches(page.BaseUrl) || !page.EnabledCompatibilityMethods.Contains(MethodId))
                throw new QueryTransportException(CandidateStatus.Unsupported, "Fireworks 控制台余额查询未启用");
            if (page.CredentialRef.ResolveClass() != CredentialClass.ApiKey)
                throw new QueryTransportException(CandidateStatus.AuthRequired, "需要 API Key 核对 Fireworks 控制台账户");
            var key = readKey is null ? new CredentialResolver(page).ReadApiKey() : readKey(page);
            if (string.IsNullOrWhiteSpace(key))
                throw new QueryTransportException(CandidateStatus.AuthRequired, "请先保存 Fireworks API Key，再登录控制台");
            if (reader is null)
                throw new QueryTransportException(CandidateStatus.AuthRequired, "请登录 Fireworks 控制台读取余额");
            var observed = await reader.ReadAsync(page, ct);
            if (string.IsNullOrWhiteSpace(observed.AccountId))
                throw new QueryTransportException(CandidateStatus.SchemaMismatch, "控制台余额或账户标识无效");
            await VerifyAccountAsync(observed.AccountId, key, ct);
            var source = new SourceIdentity("fireworks", $"console:page:{page.Id}:account:{observed.AccountId}", MethodId,
                "https://app.fireworks.ai/account/home", observed.AccountId);
            var scope = new CredentialScope(CredentialClass.ConsoleSession, "fireworks", observed.AccountId);
            return new MethodQueryResult([new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope,
                new Coverage(null, null, Scope: "控制台可用 Credits"), observed.FetchedAt, 0.9, true, false,
                observed.Balance, null, null, null, "USD", "credits", observed.FetchedAt.AddMinutes(35))],
                SnapshotStatus.Success, null, observed.FetchedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is QueryTransportException or HttpRequestException or OperationCanceledException
            or JsonException or InvalidOperationException or KeyNotFoundException or TimeoutException)
        {
            var failure = ex as QueryTransportException ?? new QueryTransportException(
                ex is HttpRequestException or OperationCanceledException or TimeoutException ? CandidateStatus.NetworkFailure : CandidateStatus.SchemaMismatch,
                ex is HttpRequestException or OperationCanceledException or TimeoutException ? "Fireworks 余额读取超时或网络失败" : "Fireworks 余额响应结构不匹配");
            return MethodQueryResult.Empty(QueryFailureClassifier.SnapshotStatusOf(failure.Status), failure.Message)
                with { Failure = new(failure.Status, failure.Message, DateTimeOffset.UtcNow, RetryAt: failure.RetryAt) };
        }
    }

    private async Task VerifyAccountAsync(string accountId, string key, CancellationToken ct)
    {
        string? token = null;
        var seen = new HashSet<string>();
        for (var page = 0; page < 20; page++)
        {
            var url = "https://api.fireworks.ai/v1/accounts?pageSize=200"
                + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await (http ?? SharedHttp).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new QueryTransportException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => CandidateStatus.AuthRequired,
                    HttpStatusCode.Forbidden => CandidateStatus.Forbidden,
                    HttpStatusCode.TooManyRequests => CandidateStatus.RateLimited,
                    _ when (int)response.StatusCode >= 500 => CandidateStatus.NetworkFailure,
                    _ => CandidateStatus.SchemaMismatch,
                }, $"Fireworks 账户核对 HTTP {(int)response.StatusCode}", (int)response.StatusCode,
                    retryAt: QueryTransportException.ReadRetryAt(response));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.GetProperty("accounts").EnumerateArray()
                .Any(account => account.GetProperty("name").GetString() == "accounts/" + accountId)) return;
            token = document.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(token))
                throw new QueryTransportException(CandidateStatus.Forbidden, "控制台账户与此页面 API Key 不一致，请切换账户后重试");
            if (!seen.Add(token)) break;
        }
        throw new QueryTransportException(CandidateStatus.SchemaMismatch, "Fireworks 账户分页无效");
    }
}
