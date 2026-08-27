using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// opencode.rolling-window.api-key：opencode Go 私有网关 + 普通 API key → 5h/周/月滚动窗口。
/// 与 OAuth 绝对值是独立方法；必须由页面显式启用。
/// </summary>
public sealed class OpenCodeRollingWindowApiKeyMethod : IQueryMethod
{
    private static readonly QueryMethodDescriptor Descriptor = new(
        "opencode.rolling-window.api-key",
        SourceKind.RollingWindowSnapshot,
        CredentialClass.ApiKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.RollingWindow),
        SourceStability.PrivateCompat,
        MethodEnablement.PrivateCompatOnly,
        DefaultPriority: 20,
        MethodSupport.ImplementationVersion);

    private const string Provider = "opencode";

    private readonly OpenCodeUsageClient _client;

    public OpenCodeRollingWindowApiKeyMethod(OpenCodeUsageClient client) => _client = client;

    public QueryMethodDescriptor Describe() => Descriptor;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        if (CredentialResolver.ProviderOf(page.BaseUrl) != Provider)
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NoReliableUsage,
                "Base URL 不匹配 opencode 网关（低置信度提示）",
                evidence: new[] { DetectionEvidence.UrlHint(page.BaseUrl) });

        if (!page.EnabledCompatibilityMethods.Contains(Descriptor.MethodId, StringComparer.Ordinal))
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.Unsupported,
                "私有兼容方法未显式启用");

        if (!context.Credentials.HasApiKey)
            return MethodSupport.AuthRequired(Descriptor, "页面未配置 opencode API key",
                DetectionEvidence.Auth("需要 Bearer API key"));

        var server = OpenCodeUsageClient.DeriveServer(page.BaseUrl);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var (rolling, weekly, monthly) = await _client.FetchWindowUsageAsync(server, context.Credentials.ReadApiKey()!, cts.Token);
            _ = rolling; _ = weekly; _ = monthly;
            return MethodSupport.Available(Descriptor, context.Credentials.Scope, Coverage.Unknown,
                new[] { DetectionEvidence.Field("/zen/go/v1/usage.usage.{rolling,weekly,monthly}") },
                source: new SourceIdentity(Provider, "api-key", Descriptor.MethodId, $"{server}/zen/go/v1/usage"),
                confidence: 90);
        }
        catch (QueryTransportException ex)
        {
            return MethodSupport.NotAvailable(Descriptor, ex.Status, ex.Message,
                evidence: new[] { DetectionEvidence.Http(ex.HttpStatus ?? 0, ex.Message) });
        }
        catch (InvalidOperationException ex)
        {
            return MethodSupport.NotAvailable(Descriptor, QueryFailureClassifier.StatusOf(ex),
                ex.Message, evidence: new[] { DetectionEvidence.Http(0, ex.Message) });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NetworkFailure, "超时/网络错误");
        }
        catch (HttpRequestException)
        {
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NetworkFailure, "超时/网络错误");
        }
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        var server = OpenCodeUsageClient.DeriveServer(page.BaseUrl);
        var capabilities = new List<CapabilityValue>();
        var key = page.CredentialRef.Target is { } target
            && CredentialStore.TryReadSecret(target, out var secret) ? secret : null;
        if (string.IsNullOrWhiteSpace(key))
            return MethodQueryResult.Empty(SnapshotStatus.AuthRequired, "opencode API key 不可用");
        var (rolling, weekly, monthly) = await _client.FetchWindowUsageAsync(server, key, ct);
        var scope = candidate.CredentialScope ?? new CredentialScope(CredentialClass.ApiKey, Provider);
        var source = candidate.Source ?? new SourceIdentity(Provider, "api-key", Descriptor.MethodId, $"{server}/zen/go/v1/usage");
        capabilities.Add(Window(rolling, "rolling", "5h滚动", scope, source));
        capabilities.Add(Window(weekly, "weekly", "周用量", scope, source));
        capabilities.Add(Window(monthly, "monthly", "月用量", scope, source));
        return new MethodQueryResult(capabilities, SnapshotStatus.Success, null, DateTimeOffset.UtcNow);
    }

    private static RollingWindowValue Window(OpenCodeUsageClient.WindowUsage w, string key, string name,
        CredentialScope scope, SourceIdentity source) => new(
        CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
        Confidence: 1.0, IsPrivate: true, IsEstimated: false,
        WindowKey: key, WindowName: name, Status: w.Status,
        Used: null, Limit: null, Remaining: null, Percent: w.Percent,
        ResetsAt: w.ResetsAt, Unit: "percent",
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15));

}
