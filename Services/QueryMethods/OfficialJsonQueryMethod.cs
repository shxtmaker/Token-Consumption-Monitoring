using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>公开只读 JSON 查询的认证、端点校验与错误边界。拒绝重定向和非官方主机。</summary>
public abstract class OfficialJsonQueryMethod : IQueryMethod
{
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private readonly HttpClient _http;
    private readonly Func<PageConfigRecord, CredentialClass, string?> _readSecret;
    protected readonly QueryMethodDescriptor Descriptor;
    protected readonly string Provider;
    private readonly string[] _hosts;
    private readonly string[] _basePaths;

    protected OfficialJsonQueryMethod(string id, string provider, CredentialClass credential,
        CapabilityKind capability, string[] hosts, string[] basePaths,
        HttpClient? http = null, Func<PageConfigRecord, CredentialClass, string?>? readSecret = null,
        SourceStability? stability = null)
    {
        Provider = provider;
        _hosts = hosts;
        _basePaths = basePaths;
        _http = http ?? SharedHttp;
        _readSecret = readSecret ?? ((page, kind) => new CredentialResolver(page).ReadSecret(kind));
        Descriptor = new(id,
            capability == CapabilityKind.BalanceOrQuota ? SourceKind.AllowanceOrBalance
                : capability == CapabilityKind.RollingWindow ? SourceKind.RollingWindowSnapshot : SourceKind.RemoteOfficialStats,
            credential, QueryMethodDescriptor.CapabilitiesOf(capability),
            stability ?? (credential == CredentialClass.ApiKey ? SourceStability.OfficialStable : SourceStability.OfficialConditional),
            MethodEnablement.Conditional, 30, "1.0.0");
    }

    public QueryMethodDescriptor Describe() => Descriptor;

    internal bool AcceptsEndpoint(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && _hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase)
        && _basePaths.Contains(uri.AbsolutePath.TrimEnd('/'), StringComparer.Ordinal);

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        if (!AcceptsEndpoint(page.BaseUrl))
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.Unsupported, "此方法需要匹配的官方 HTTPS API 地址");
        if (page.CredentialRef.ResolveClass() != Descriptor.CredentialClass)
            return MethodSupport.AuthRequired(Descriptor, $"需要 {Descriptor.CredentialClass} 类型的凭据");
        var result = await QueryAsync(page, null!, ct);
        if (result.Status is not (SnapshotStatus.Success or SnapshotStatus.NoData))
            return MethodSupport.NotAvailable(Descriptor, result.Failure?.Status ?? CandidateStatus.SchemaMismatch,
                result.Failure?.Reason ?? "官方查询未通过") with { Failure = result.Failure };
        return MethodSupport.Available(Descriptor, Scope(page), result.Capabilities.FirstOrDefault()?.Coverage ?? Coverage.Unknown,
            new[] { DetectionEvidence.Field("官方响应结构已验证") }, Identity(page), 95);
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        try
        {
            if (!AcceptsEndpoint(page.BaseUrl)) throw Failure(CandidateStatus.Unsupported, "官方 API 地址不匹配");
            if (page.CredentialRef.ResolveClass() != Descriptor.CredentialClass)
                throw Failure(CandidateStatus.AuthRequired, "凭据类型不匹配");
            var key = _readSecret(page, Descriptor.CredentialClass);
            if (string.IsNullOrWhiteSpace(key)) throw Failure(CandidateStatus.AuthRequired, "未配置查询所需的密钥");
            var values = await FetchAsync(page, key, ct);
            return new(values, values.Count == 0 ? SnapshotStatus.NoData : SnapshotStatus.Success, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is QueryTransportException or HttpRequestException or OperationCanceledException
            or ArgumentException or JsonException or FormatException or OverflowException or InvalidOperationException or KeyNotFoundException)
        {
            var error = ex as QueryTransportException ?? Failure(
                ex is HttpRequestException or OperationCanceledException ? CandidateStatus.NetworkFailure : CandidateStatus.SchemaMismatch,
                ex is HttpRequestException or OperationCanceledException ? "官方查询网络失败或超时" : "官方响应字段缺失或类型不匹配");
            return MethodQueryResult.Empty(QueryFailureClassifier.SnapshotStatusOf(error.Status), error.Message)
                with { Failure = new(error.Status, error.Message, DateTimeOffset.UtcNow, RetryAt: error.RetryAt) };
        }
    }

    protected abstract Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct);

    protected async Task<JsonDocument> GetAsync(PageConfigRecord page, string path, string key, CancellationToken ct)
    {
        var origin = new Uri(page.BaseUrl).GetLeftPart(UriPartial.Authority);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin + path);
        ApplyAuthentication(request, key);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => CandidateStatus.AuthRequired,
                HttpStatusCode.Forbidden => CandidateStatus.Forbidden,
                HttpStatusCode.TooManyRequests => CandidateStatus.RateLimited,
                _ when (int)response.StatusCode >= 500 => CandidateStatus.NetworkFailure,
                _ => CandidateStatus.SchemaMismatch,
            };
            throw new QueryTransportException(status, $"{Provider} 查询 HTTP {(int)response.StatusCode}",
                (int)response.StatusCode, retryAt: QueryTransportException.ReadRetryAt(response));
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    protected virtual void ApplyAuthentication(HttpRequestMessage request, string key)
    {
        if (Provider == "anthropic")
        {
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    protected CredentialScope Scope(PageConfigRecord page) => new(Descriptor.CredentialClass, Provider);
    protected SourceIdentity Identity(PageConfigRecord page) => new(Provider,
        $"{Descriptor.CredentialClass}:page:{page.Id}", Descriptor.MethodId, new Uri(page.BaseUrl).GetLeftPart(UriPartial.Authority));
    protected static QueryTransportException Failure(CandidateStatus status, string reason) => new(status, reason);
    protected static QueryTransportException Schema(string reason) => Failure(CandidateStatus.SchemaMismatch, reason);
    protected static decimal Number(JsonElement obj, string field) => OptionalNumber(obj, field) ?? throw Schema($"缺少数值字段 {field}");
    protected static decimal? OptionalNumber(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number)) return number;
        throw Schema($"字段 {field} 不是有效数值");
    }
    protected static long Count(JsonElement obj, string field)
    {
        var value = Number(obj, field);
        if (value < 0 || value != decimal.Truncate(value)) throw Schema($"字段 {field} 不是非负整数");
        return checked((long)value);
    }
    protected static string Text(JsonElement obj, string field)
        => obj.GetProperty(field).GetString() is { Length: > 0 } value ? value : throw Schema($"缺少字段 {field}");
    protected BalanceQuotaValue Balance(PageConfigRecord page, decimal? balance, string currency,
        decimal? used = null, decimal? limit = null, decimal? remaining = null, Coverage? coverage = null)
        => new(CapabilityKind.BalanceOrQuota, Identity(page), Scope(page), coverage ?? Coverage.Unknown, DateTimeOffset.UtcNow,
            1, false, false, balance, used, limit, remaining, currency, null, DateTimeOffset.UtcNow.AddMinutes(5));
}
