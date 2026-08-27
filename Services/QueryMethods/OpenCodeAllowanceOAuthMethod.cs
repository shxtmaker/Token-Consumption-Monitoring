using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// opencode.allowance.oauth：opencode Go 私有端点 + 全局 OAuth 会话 → 窗口绝对额度。
/// 必须由页面显式启用；缺少 OAuth 不影响其他来源。
/// </summary>
public sealed class OpenCodeAllowanceOAuthMethod : IQueryMethod
{
    private static readonly QueryMethodDescriptor Descriptor = new(
        "opencode.allowance.oauth",
        SourceKind.AllowanceOrBalance,
        CredentialClass.OAuthSession,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.RollingWindow, CapabilityKind.BalanceOrQuota),
        SourceStability.PrivateCompat,
        MethodEnablement.PrivateCompatOnly,
        DefaultPriority: 30,
        MethodSupport.ImplementationVersion);

    private const string Provider = "opencode";

    private readonly OpenCodeUsageClient _client;
    private readonly OpenCodeAuthService _auth;

    public OpenCodeAllowanceOAuthMethod(OpenCodeUsageClient client, OpenCodeAuthService auth)
    {
        _client = client;
        _auth = auth;
    }

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

        await Task.CompletedTask;
        if (!_auth.IsLoggedIn)
            return MethodSupport.AuthRequired(Descriptor, "需要 opencode 全局 OAuth 会话",
                DetectionEvidence.Auth("设备码登录后可用"));

        var tokens = await _auth.EnsureFreshAsync(ct);
        if (tokens is null)
            return MethodSupport.AuthRequired(Descriptor, "opencode OAuth token 不可用（刷新失败）");

        var server = OpenCodeUsageClient.DeriveServer(page.BaseUrl);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var meters = await _client.FetchGoStatusAsync(server, tokens.AccessToken, tokens.OrgId, cts.Token);
            if (meters.Count == 0)
                return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NoReliableUsage,
                    "OAuth 会话可用但无额度 meters", evidence: new[] { DetectionEvidence.Field("/api/go/status.meters 为空") });

            return MethodSupport.Available(Descriptor, new CredentialScope(CredentialClass.OAuthSession, Provider),
                Coverage.Unknown,
                new[] { DetectionEvidence.Field($"/api/go/status.meters × {meters.Count}") },
                source: new SourceIdentity(Provider, "oauth", Descriptor.MethodId, $"{server}/api/go/status"),
                confidence: 88);
        }
        catch (QueryTransportException ex)
        {
            return MethodSupport.NotAvailable(Descriptor, ex.Status, ex.Message);
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
        var capabilities = new List<CapabilityValue>();
        var tokens = await _auth.EnsureFreshAsync(ct);
        if (tokens is not null)
        {
            var server = OpenCodeUsageClient.DeriveServer(page.BaseUrl);
            var meters = await _client.FetchGoStatusAsync(server, tokens.AccessToken, tokens.OrgId, ct);
            var scope = candidate.CredentialScope ?? new CredentialScope(CredentialClass.OAuthSession, Provider);
            var source = candidate.Source ?? new SourceIdentity(Provider, "oauth", Descriptor.MethodId, $"{server}/api/go/status");
            foreach (var m in meters)
            {
                var key = m.Kind switch
                {
                    "five_hour" => "five_hour",
                    "calendar_week" => "calendar_week",
                    "product_period" => "product_period",
                    _ => m.Kind,
                };
                capabilities.Add(new RollingWindowValue(
                    CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
                    Confidence: 1.0, IsPrivate: true, IsEstimated: false,
                    WindowKey: $"absolute.{key}", WindowName: m.Kind switch
                    {
                        "five_hour" => "5h 额度",
                        "calendar_week" => "周额度",
                        "product_period" => "周期额度",
                        _ => m.Kind,
                    },
                    Status: "正常",
                    Used: m.UsedMicroCents, Limit: m.LimitMicroCents, Remaining: m.RemainingMicroCents,
                    Percent: m.LimitMicroCents is { } l && l > 0 && m.RemainingMicroCents is { } r
                        ? (int)Math.Round(Math.Max(0, Math.Min(100, (double)(l - r) / l * 100))) : (int?)null,
                    ResetsAt: m.ResetsAt, Unit: "microCents"));
            }
        }
        return new MethodQueryResult(capabilities,
            capabilities.Count > 0 ? SnapshotStatus.Success : SnapshotStatus.NoData,
            capabilities.Count > 0 ? null : new FailureInfo(
                tokens is null ? CandidateStatus.AuthRequired : CandidateStatus.NoReliableUsage,
                tokens is null ? "OAuth 会话不可用" : "OAuth 会话无可用额度", DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
    }
}
