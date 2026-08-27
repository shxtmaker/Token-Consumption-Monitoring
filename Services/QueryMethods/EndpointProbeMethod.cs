using System.Net;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// endpoint.probe：通用连接、鉴权和模型目录探测。
/// /models 成功只能形成 ProbeDiagnostic，不能升级为用量能力。
/// </summary>
public sealed class EndpointProbeMethod : IQueryMethod
{
    private static readonly QueryMethodDescriptor Descriptor = new(
        "endpoint.probe",
        SourceKind.Probe,
        CredentialClass.ApiKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ProbeDiagnostic),
        SourceStability.ProbeOnly,
        MethodEnablement.Always,
        DefaultPriority: 100,
        MethodSupport.ImplementationVersion);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly DeepSeekSessionService? _deepSeekSession;

    public EndpointProbeMethod(OpenCodeAuthService? openCodeAuth = null, DeepSeekSessionService? deepSeekSession = null)
        => _deepSeekSession = deepSeekSession;

    public QueryMethodDescriptor Describe() => Descriptor;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        var evidence = new List<DetectionEvidence>
        {
            DetectionEvidence.UrlHint($"Base URL {page.BaseUrl} 作为低置信度提示"),
        };

        if (page.ParseProtocol() == KeyFormat.Protocol.DeepSeekConsole)
        {
            if (_deepSeekSession is not { IsLoggedIn: true })
                return MethodSupport.AuthRequired(Descriptor, "控制台会话未登录", evidence.ToArray());
            return MethodSupport.Available(Descriptor, context.Credentials.Scope, Coverage.Unknown, evidence,
                source: new SourceIdentity("probe", "endpoint", Descriptor.MethodId, page.BaseUrl), confidence: 40);
        }

        if (!context.Credentials.HasApiKey)
            return MethodSupport.AuthRequired(Descriptor, "页面未配置 API key", evidence.ToArray());

        var result = await ProbeWithModelsAsync(page, context.Credentials.ReadApiKey(), ct);
        if (!result.Ok)
            return MethodSupport.NotAvailable(Descriptor, result.Status, result.Error, 0, evidence.ToArray());

        evidence.Add(DetectionEvidence.Field("/models"));
        return MethodSupport.Available(Descriptor, context.Credentials.Scope, Coverage.Unknown, evidence,
            source: new SourceIdentity("probe", "endpoint", Descriptor.MethodId, page.BaseUrl), confidence: 60);
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        if (page.ParseProtocol() == KeyFormat.Protocol.DeepSeekConsole)
        {
            var loggedIn = _deepSeekSession is { IsLoggedIn: true };
            var consoleValue = ProbeValue(candidate, page, loggedIn, loggedIn, Array.Empty<string>(),
                loggedIn ? null : "控制台会话未登录");
            return new MethodQueryResult(
                new CapabilityValue[] { consoleValue },
                loggedIn ? SnapshotStatus.ProbeOnly : SnapshotStatus.AuthRequired,
                loggedIn ? null : new FailureInfo(CandidateStatus.AuthRequired, "控制台会话未登录", DateTimeOffset.UtcNow),
                consoleValue.FetchedAt);
        }

        var result = await ProbeWithModelsAsync(page, ReadPageKey(page), ct);
        var value = ProbeValue(candidate, page, result.Ok, result.Authenticated, result.Models,
            result.Ok ? null : result.Error);
        return new MethodQueryResult(
            new CapabilityValue[] { value },
            result.Ok ? SnapshotStatus.ProbeOnly : QueryFailureClassifier.SnapshotStatusOf(result.Status),
            result.Ok ? null : new FailureInfo(result.Status, result.Error, DateTimeOffset.UtcNow),
            value.FetchedAt);
    }

    private async Task<ProbeResult> ProbeWithModelsAsync(PageConfigRecord page, string? key, CancellationToken ct)
    {
        var models = new List<string>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var (url, anthropicVersion) = BuildRequest(page, "/models");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, page, key, anthropicVersion);
            using var response = await _http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
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
                return new ProbeResult(false, status, $"HTTP {(int)response.StatusCode}", false, models);
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return new ProbeResult(false, CandidateStatus.SchemaMismatch, "响应缺少 data 数组", true, models);
            foreach (var model in data.EnumerateArray())
                if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    models.Add(id.GetString()!);
            return new ProbeResult(true, CandidateStatus.Available, "", true, models);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, CandidateStatus.NetworkFailure, "请求超时", false, models);
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, CandidateStatus.NetworkFailure, "网络错误", false, models);
        }
        catch (JsonException)
        {
            return new ProbeResult(false, CandidateStatus.SchemaMismatch, "响应不是有效 JSON", false, models);
        }
    }

    private static ProbeDiagnosticValue ProbeValue(
        MethodCandidate candidate,
        PageConfigRecord page,
        bool connected,
        bool authenticated,
        IReadOnlyList<string> models,
        string? diagnostic)
        => new(
            CapabilityKind.ProbeDiagnostic,
            candidate.Source ?? new SourceIdentity("probe", "endpoint", Descriptor.MethodId, page.BaseUrl),
            candidate.CredentialScope ?? new CredentialScope(CredentialClass.None),
            Coverage.Unknown,
            DateTimeOffset.UtcNow,
            Confidence: connected ? 0.9 : 0.2,
            IsPrivate: false,
            IsEstimated: false,
            Connected: connected,
            Authenticated: authenticated,
            Models: models,
            Diagnostic: diagnostic);

    private static string? ReadPageKey(PageConfigRecord page)
        => page.CredentialRef.Target is { } target
           && CredentialStore.TryReadSecret(target, out var key)
           && !string.IsNullOrWhiteSpace(key) ? key : null;

    private static (string Url, string? AnthropicVersion) BuildRequest(PageConfigRecord page, string path)
    {
        var baseUrl = page.BaseUrl.TrimEnd('/');
        var protocol = page.ParseProtocol();
        var url = protocol == KeyFormat.Protocol.Anthropic ? $"{baseUrl}/v1{path}" : $"{baseUrl}{path}";
        return (url, protocol == KeyFormat.Protocol.Anthropic ? "2023-06-01" : null);
    }

    private static void ApplyAuth(HttpRequestMessage request, PageConfigRecord page, string? key, string? anthropicVersion)
    {
        if (page.ParseProtocol() == KeyFormat.Protocol.Anthropic)
        {
            if (!string.IsNullOrEmpty(key)) request.Headers.Add("x-api-key", key);
            if (!string.IsNullOrEmpty(anthropicVersion)) request.Headers.Add("anthropic-version", anthropicVersion);
        }
        else if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
    }

    private sealed record ProbeResult(
        bool Ok,
        CandidateStatus Status,
        string Error,
        bool Authenticated,
        IReadOnlyList<string> Models);
}
