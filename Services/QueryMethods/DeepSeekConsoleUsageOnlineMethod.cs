using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// deepseek.console-usage.online：DeepSeek 控制台「今日用量」（线上拉取，DeepSeek 官方账号页面唯一用量来源）。
///
/// 无官方 API-key 用量端点——平台用量仅存在于 platform.deepseek.com 私有接口
/// （/api/v0/usage/by_api_key/{amount,cost}），需登录控制台会话（非 sk- key）。
/// 本方法把原先「需显式启用」的私有兼容用量升级为 DeepSeek 官方账号页面的常开线上来源：
/// 页面协议为 DeepSeekConsole 且会话已登录即可参与扫描并自动被选为今日用量来源。
///
/// 数据经 DeepSeekSessionService（WebView2 页面上下文）拉取：
/// 优先页面自身请求捕获（风控上下文完整），失败回退注入 XHR。
/// </summary>
public sealed class DeepSeekConsoleUsageOnlineMethod : IQueryMethod
{
    private static readonly QueryMethodDescriptor Descriptor = new(
        "deepseek.console-usage.online",
        SourceKind.ConsoleOrPrivateUI,
        CredentialClass.ConsoleSession,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ReportedUsage, CapabilityKind.ReportedCost),
        SourceStability.OfficialStable,
        MethodEnablement.Always,
        DefaultPriority: 20,
        MethodSupport.ImplementationVersion);

    private const string Provider = "deepseek";
    private readonly DeepSeekSessionService _session;
    private readonly DeepSeekUsageClient _usage;

    public DeepSeekConsoleUsageOnlineMethod(DeepSeekSessionService session, DeepSeekUsageClient usage)
    {
        _session = session;
        _usage = usage;
    }

    public QueryMethodDescriptor Describe() => Descriptor;

    public Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        // 该来源只适用于 DeepSeek 官方页面（控制台会话协议/控制台会话凭据）。
        var isDeepSeekConsolePage =
            page.ParseProtocol() == KeyFormat.Protocol.DeepSeekConsole
            || page.CredentialRef.ResolveClass() == CredentialClass.ConsoleSession;
        if (!isDeepSeekConsolePage)
            return Task.FromResult(MethodSupport.NotAvailable(Descriptor, CandidateStatus.Unsupported,
                "仅 DeepSeek 官方控制台页面提供线上用量",
                evidence: new[] { DetectionEvidence.UrlHint(page.BaseUrl) }));

        if (!_session.IsLoggedIn)
            return Task.FromResult(MethodSupport.AuthRequired(Descriptor, "DeepSeek 控制台会话未登录",
                DetectionEvidence.Auth("WebView2 会话登录后可用")));

        // 不阻塞扫描：会话已登录即可作为候选；具体数据在查询阶段拉取。
        return Task.FromResult(MethodSupport.Available(Descriptor, context.Credentials.Scope, Coverage.Unknown,
            new[] { DetectionEvidence.Field("控制台会话已登录") },
            source: new SourceIdentity(Provider, "console-session", Descriptor.MethodId, "https://platform.deepseek.com/usage"),
            confidence: 85));
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        if (!_session.IsLoggedIn)
            return MethodQueryResult.Empty(SnapshotStatus.AuthRequired, "DeepSeek 控制台会话失效");

        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Date, TimeZoneInfo.Local.GetUtcOffset(now));
        var tzSec = (int)TimeZoneInfo.Local.BaseUtcOffset.TotalSeconds;
        var byModel = await _usage.FetchTodayByModelAsync(
            todayStart.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), tzSec, ct);

        var scope = candidate.CredentialScope ?? new CredentialScope(CredentialClass.ConsoleSession, Provider);
        var source = candidate.Source ?? new SourceIdentity(Provider, "console-session", Descriptor.MethodId, "https://platform.deepseek.com/usage");

        long total = 0; long totalRequests = 0; decimal totalCost = 0;
        var rows = new List<ModelUsageRow>(byModel.Count);
        foreach (var (model, u) in byModel)
        {
            var tokens = u.CacheHitTokens + u.CacheMissTokens + u.ResponseTokens;
            total += tokens;
            totalRequests += u.RequestCount;
            totalCost += u.CostCny;
            rows.Add(new ModelUsageRow(model, tokens, u.CostCny, "CNY",
                new TokenBreakdown(u.CacheMissTokens, u.ResponseTokens, u.CacheHitTokens, 0)));
        }

        var capabilities = new List<CapabilityValue>
        {
            new ReportedUsageValue(CapabilityKind.ReportedUsage, source, scope,
                new Coverage(todayStart, now, Granularity.PerModel), DateTimeOffset.UtcNow,
                Confidence: 1.0, IsPrivate: true, IsEstimated: false,
                TotalTokens: total, TotalRequests: totalRequests, Models: rows),
            new ReportedCostValue(CapabilityKind.ReportedCost, source, scope,
                new Coverage(todayStart, now, Granularity.PerModel), DateTimeOffset.UtcNow,
                Confidence: 1.0, IsPrivate: true, IsEstimated: false,
                Amount: totalCost, Currency: "CNY"),
        };
        return new MethodQueryResult(capabilities, SnapshotStatus.Success, null, DateTimeOffset.UtcNow);
    }
}
