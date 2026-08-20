using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Adapters;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// endpoint.probe：通用连接/鉴权/模型目录探测。
/// 只提供 ProbeDiagnostic 能力；/models 成功不能升级为用量方法，模型目录不进入展示列表。
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
    private readonly OpenCodeAuthService? _openCodeAuth;
    private readonly DeepSeekSessionService? _deepSeekSession;

    public EndpointProbeMethod(OpenCodeAuthService? openCodeAuth = null, DeepSeekSessionService? deepSeekSession = null)
    {
        _openCodeAuth = openCodeAuth;
        _deepSeekSession = deepSeekSession;
    }

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

        var (ok, error) = await ProbeModelsAsync(page, context.Credentials.ReadApiKey()!, ct);
        evidence.Add(DetectionEvidence.Field("/models"));
        if (!ok)
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NetworkFailure, error, 0, evidence.ToArray());

        return MethodSupport.Available(Descriptor, context.Credentials.Scope, Coverage.Unknown, evidence,
            source: new SourceIdentity("probe", "endpoint", Descriptor.MethodId, page.BaseUrl), confidence: 60);
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        var (ok, error, authenticated, models) = await ProbeWithModelsAsync(page, ct);
        var value = new ProbeDiagnosticValue(
            CapabilityKind.ProbeDiagnostic,
            candidate.Source ?? new SourceIdentity("probe", "endpoint", Descriptor.MethodId, page.BaseUrl),
            candidate.CredentialScope ?? new CredentialScope(CredentialClass.None),
            Coverage.Unknown,
            DateTimeOffset.UtcNow,
            Confidence: ok ? 0.9 : 0.2,
            IsPrivate: false,
            IsEstimated: false,
            Connected: ok,
            Authenticated: authenticated,
            Models: models,
            Diagnostic: ok ? null : error);

        var status = ok ? SnapshotStatus.ProbeOnly : SnapshotStatus.PermanentFailure;
        return new MethodQueryResult(new CapabilityValue[] { value }, status,
            ok ? null : new FailureInfo(CandidateStatus.NetworkFailure, error, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
    }

    private async Task<(bool Ok, string Error)> ProbeModelsAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        var (ok, error, _, _) = await ProbeWithModelsAsync(page, ct);
        return (ok, error);
    }

    private async Task<(bool Ok, string Error, bool Authenticated, IReadOnlyList<string> Models)> ProbeWithModelsAsync(PageConfigRecord page, CancellationToken ct)
    {
        var models = new List<string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var (url, anthropicVersion) = BuildRequest(page, "/models");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var key = CredentialStore.TryReadSecret(page.CredentialRef.Target!, out var k) ? k : null;
            ApplyAuth(request, page, key, anthropicVersion);
            using var response = await _http.SendAsync(request, cts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (false, "401 密钥无效", false, models);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return (false, "403 无权限", false, models);
            if (!response.IsSuccessStatusCode) return (false, $"HTTP {(int)response.StatusCode}", false, models);

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var m in data.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
                        models.Add(id.GetString()!);
                }
            }
            return (true, "", true, models);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (false, "超时/网络错误", false, models);
        }
    }

    private static (string Url, string? AnthropicVersion) BuildRequest(PageConfigRecord page, string path)
    {
        var baseUrl = page.BaseUrl.TrimEnd('/');
        var protocol = page.ParseProtocol();
        var url = protocol == KeyFormat.Protocol.Anthropic ? $"{baseUrl}/v1{path}" : $"{baseUrl}{path}";
        return (url, protocol == KeyFormat.Protocol.Anthropic ? "2023-06-01" : null);
    }

    private static void ApplyAuth(HttpRequestMessage request, PageConfigRecord page, string? key, string? anthropicVersion)
    {
        var protocol = page.ParseProtocol();
        if (protocol == KeyFormat.Protocol.Anthropic)
        {
            if (!string.IsNullOrEmpty(key)) request.Headers.Add("x-api-key", key);
            if (!string.IsNullOrEmpty(anthropicVersion)) request.Headers.Add("anthropic-version", anthropicVersion);
        }
        else if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
    }
}
