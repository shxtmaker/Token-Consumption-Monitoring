using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// commandcode.allowance-window.compat：Command Code /alpha 私有控制面的窗口额度（5h/周）与月额度兼容方法。
/// 私有兼容来源：仅 commandcode.ai 页面（显式配置）启用；monthlyCredits 缺失时不伪造月额度，
/// 不复用全量历史 totalCost 兜底当前周期消费。套餐名称仅作元数据，不参与方法选择。
/// </summary>
public sealed class CommandCodeAllowanceWindowMethod : IQueryMethod
{
    private static readonly QueryMethodDescriptor Descriptor = new(
        "commandcode.allowance-window.compat",
        SourceKind.RollingWindowSnapshot,
        CredentialClass.ApiKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.RollingWindow, CapabilityKind.BalanceOrQuota),
        SourceStability.PrivateCompat,
        MethodEnablement.PrivateCompatOnly,
        DefaultPriority: 45,
        MethodSupport.ImplementationVersion);

    private const string Provider = "commandcode";
    private readonly CommandCodeUsageClient _client;

    public CommandCodeAllowanceWindowMethod(CommandCodeUsageClient client) => _client = client;

    public QueryMethodDescriptor Describe() => Descriptor;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        if (CredentialResolver.ProviderOf(page.BaseUrl) != Provider)
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NoReliableUsage,
                "Base URL 不匹配 Command Code（低置信度提示）",
                evidence: new[] { DetectionEvidence.UrlHint(page.BaseUrl) });

        var key = ResolveKey(page);
        if (key is null)
            return MethodSupport.AuthRequired(Descriptor, "未配置 Command Code API key（页面 key 或本地 CLI 登录）",
                DetectionEvidence.Auth("页面凭据优先；~/.commandcode/auth.json 作为本地凭据发现来源"));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await _client.FetchOrgIdAsync(key, cts.Token);
            return MethodSupport.Available(Descriptor, context.Credentials.Scope, Coverage.Unknown,
                new[] { DetectionEvidence.Field("/alpha/whoami OK") },
                source: new SourceIdentity(Provider, "api-key", Descriptor.MethodId, "https://api.commandcode.ai/alpha"),
                confidence: 80);
        }
        catch (CommandCodeAuthException)
        {
            return MethodSupport.AuthRequired(Descriptor, "Command Code key 无效（401/UNAUTHORIZED）");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NetworkFailure, "超时/网络错误");
        }
        catch (InvalidOperationException ex)
        {
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NetworkFailure, ex.Message);
        }
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        var key = ResolveKey(page);
        if (key is null)
            return new MethodQueryResult(Array.Empty<CapabilityValue>(), SnapshotStatus.AuthRequired,
                new FailureInfo(CandidateStatus.AuthRequired, "Command Code key 不可用", DateTimeOffset.UtcNow),
                DateTimeOffset.UtcNow);

        var usage = await _client.FetchUsageAsync(key, ct);
        var capabilities = new List<CapabilityValue>();
        var scope = candidate.CredentialScope ?? new CredentialScope(CredentialClass.ApiKey, Provider);
        var source = candidate.Source ?? new SourceIdentity(Provider, "api-key", Descriptor.MethodId, "https://api.commandcode.ai/alpha");
        if (usage is null)
            return MethodQueryResult.Empty(SnapshotStatus.TemporaryFailure, "Command Code 无用量数据");

        AddWindow(capabilities, usage.Credits?.Limits?.FiveHour, "commandcode.fiveHour", "5h 窗口", scope, source);
        AddWindow(capabilities, usage.Credits?.Limits?.Weekly, "commandcode.weekly", "周窗口", scope, source);

        // 月额度：仅当存在套餐月上限与计费周期时发布；缺失时省略而不是伪造
        var monthlyLimit = usage.Plan?.MonthlyCredits;
        if (monthlyLimit is { } limit && usage.Subscription?.CurrentPeriodEnd is { } periodEnd)
        {
            // 月剩余优先取 API 返回的 monthlyCredits；缺失时回退 套餐总额 − 周期消费（标记 estimated）
            double? remaining = usage.Credits?.MonthlyCredits;
            var fromTotalCost = false;
            if (remaining is null && usage.TotalCost is { } cost)
            {
                remaining = Math.Max(0, (double)limit - cost);
                fromTotalCost = true;
            }
            if (remaining is { } rem)
            {
                capabilities.Add(new BalanceQuotaValue(
                    CapabilityKind.BalanceOrQuota, source, scope,
                    new Coverage(usage.Subscription.CurrentPeriodStart, periodEnd, Granularity.PerWindow), DateTimeOffset.UtcNow,
                    Confidence: 0.95, IsPrivate: true, IsEstimated: fromTotalCost,
                    Balance: null, Used: Math.Max(0, (decimal)limit - (decimal)rem), Limit: limit, Remaining: (decimal)rem,
                    Currency: "USD", Unit: "monthly-credits", ExpiresAt: periodEnd));
            }
        }

        return new MethodQueryResult(capabilities,
            capabilities.Count > 0 ? SnapshotStatus.Success : SnapshotStatus.NoData,
            capabilities.Count > 0 ? null : new FailureInfo(CandidateStatus.NoReliableUsage, "未解析到窗口/额度能力", DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
    }

    private static void AddWindow(List<CapabilityValue> list, CommandCodeUsageClient.WindowLimit? w, string key, string name,
        CredentialScope scope, SourceIdentity source)
    {
        if (w is null || w.Cap <= 0) return;
        list.Add(new RollingWindowValue(
            CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
            Confidence: 1.0, IsPrivate: true, IsEstimated: false,
            WindowKey: key, WindowName: name, Status: "正常",
            Used: w.LimitMicroCents, Limit: w.LimitMicroCents, Remaining: w.RemainingMicroCents,
            Percent: w.Percent, ResetsAt: w.ResetAt, Unit: "microCents"));
    }

    /// <summary>key 优先级：页面凭据 → 本地 CLI 登录 apiKey（本地凭据发现，不复制到页面配置）。</summary>
    private static string? ResolveKey(PageConfigRecord page)
    {
        var pageKey = CredentialStore.TryReadSecret(page.CredentialRef.Target!, out var k) && !string.IsNullOrWhiteSpace(k)
            ? k : null;
        return pageKey ?? CommandCodeUsageClient.ReadLocalApiKey();
    }
}
