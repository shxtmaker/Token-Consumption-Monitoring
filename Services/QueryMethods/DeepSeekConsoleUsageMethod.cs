using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// deepseek.console-usage.compat：DeepSeek 控制台私有会话用量（WebView2 会话，按模型 token + 金额）。
/// 私有兼容来源：只有页面显式为控制台会话（CredentialRef.ConsoleSession 或 DeepSeekConsole 协议）时才启用，
/// 不升级为官方稳定历史 API，也不无条件绑定到 API 余额页面。
/// </summary>
public sealed class DeepSeekConsoleUsageMethod : IQueryMethod
{
    private static readonly QueryMethodDescriptor Descriptor = new(
        "deepseek.console-usage.compat",
        SourceKind.ConsoleOrPrivateUI,
        CredentialClass.ConsoleSession,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ReportedUsage, CapabilityKind.ReportedCost),
        SourceStability.PrivateCompat,
        MethodEnablement.PrivateCompatOnly,
        DefaultPriority: 50,
        MethodSupport.ImplementationVersion);

    private const string Provider = "deepseek";
    private readonly DeepSeekSessionService _session;
    private readonly DeepSeekUsageClient _usage;

    public DeepSeekConsoleUsageMethod(DeepSeekSessionService session, DeepSeekUsageClient usage)
    {
        _session = session;
        _usage = usage;
    }

    public QueryMethodDescriptor Describe() => Descriptor;

    /// <summary>仅旧配置或显式控制台会话页面启用（页面凭据类别为 ConsoleSession / 协议为 DeepSeekConsole）。</summary>
    private static bool IsEnabled(PageConfigRecord page) =>
        page.CredentialRef.ResolveClass() == CredentialClass.ConsoleSession ||
        page.ParseProtocol() == KeyFormat.Protocol.DeepSeekConsole;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        if (CredentialResolver.ProviderOf(page.BaseUrl) != Provider)
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NoReliableUsage,
                "Base URL 不匹配 DeepSeek（低置信度提示）",
                evidence: new[] { DetectionEvidence.UrlHint(page.BaseUrl) });

        if (!IsEnabled(page))
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.Unsupported,
                "私有兼容方法：仅旧配置或显式控制台会话启用");

        if (!_session.IsLoggedIn)
            return MethodSupport.AuthRequired(Descriptor, "未登录 DeepSeek 控制台",
                DetectionEvidence.Auth("WebView2 会话登录后可用"));

        await Task.CompletedTask;
        return MethodSupport.Available(Descriptor, context.Credentials.Scope, Coverage.Unknown,
            new[] { DetectionEvidence.Field("控制台会话已登录") },
            source: new SourceIdentity(Provider, "console-session", Descriptor.MethodId, "https://platform.deepseek.com/usage"),
            confidence: 75);
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        if (!_session.IsLoggedIn)
            return new MethodQueryResult(Array.Empty<CapabilityValue>(), SnapshotStatus.AuthRequired,
                new FailureInfo(CandidateStatus.AuthRequired, "DeepSeek 控制台会话失效", DateTimeOffset.UtcNow),
                DateTimeOffset.UtcNow);

        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Date, TimeZoneInfo.Local.GetUtcOffset(now));
        var tzSec = (int)TimeZoneInfo.Local.BaseUtcOffset.TotalSeconds;
        var byModel = await _usage.FetchTodayByModelAsync(
            todayStart.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), tzSec);

        var scope = candidate.CredentialScope ?? new CredentialScope(CredentialClass.ConsoleSession, Provider);
        var source = candidate.Source ?? new SourceIdentity(Provider, "console-session", Descriptor.MethodId, "https://platform.deepseek.com/usage");

        long total = 0; decimal totalCost = 0;
        var rows = new List<ModelUsageRow>(byModel.Count);
        foreach (var (model, u) in byModel)
        {
            var tokens = u.CacheHitTokens + u.CacheMissTokens + u.ResponseTokens;
            total += tokens;
            totalCost += u.CostCny;
            rows.Add(new ModelUsageRow(model, tokens, u.CostCny, "CNY",
                new TokenBreakdown(u.CacheMissTokens, u.ResponseTokens, u.CacheHitTokens, 0)));
        }

        var capabilities = new List<CapabilityValue>
        {
            new ReportedUsageValue(CapabilityKind.ReportedUsage, source, scope,
                new Coverage(todayStart, now, Granularity.PerModel), DateTimeOffset.UtcNow,
                Confidence: 1.0, IsPrivate: true, IsEstimated: false,
                TotalTokens: total, TotalRequests: 0, Models: rows),
            new ReportedCostValue(CapabilityKind.ReportedCost, source, scope,
                new Coverage(todayStart, now, Granularity.PerModel), DateTimeOffset.UtcNow,
                Confidence: 1.0, IsPrivate: true, IsEstimated: false,
                Amount: totalCost, Currency: "CNY"),
        };
        return new MethodQueryResult(capabilities, SnapshotStatus.Success, null, DateTimeOffset.UtcNow);
    }
}
