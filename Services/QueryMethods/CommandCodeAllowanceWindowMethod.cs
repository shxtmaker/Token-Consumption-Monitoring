using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// commandcode.allowance-window.compat：Command Code /alpha 私有控制面的窗口额度（5h/周）。
/// 私有兼容来源：仅在页面显式启用后参与；只保留服务端直接返回的窗口字段。
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
    private readonly ICommandCodeUsageClient _client;

    public CommandCodeAllowanceWindowMethod(ICommandCodeUsageClient client) => _client = client;

    public QueryMethodDescriptor Describe() => Descriptor;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        if (CredentialResolver.ProviderOf(page.BaseUrl) != Provider)
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NoReliableUsage,
                "Base URL 不匹配 Command Code（低置信度提示）",
                evidence: new[] { DetectionEvidence.UrlHint(page.BaseUrl) });

        if (!page.EnabledCompatibilityMethods.Contains(Descriptor.MethodId, StringComparer.Ordinal))
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.Unsupported,
                "私有兼容方法未显式启用");

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
        catch (InvalidOperationException ex)
        {
            return MethodSupport.NotAvailable(Descriptor, QueryFailureClassifier.StatusOf(ex), ex.Message);
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
        var scope = candidate.CredentialScope ?? new CredentialScope(CredentialClass.ApiKey, Provider);
        var source = candidate.Source ?? new SourceIdentity(Provider, "api-key", Descriptor.MethodId, "https://api.commandcode.ai/alpha");
        if (usage is null)
            return MethodQueryResult.Empty(SnapshotStatus.TemporaryFailure, "Command Code 无用量数据");

        return BuildResult(usage, scope, source);
    }

    /// <summary>用量数据 → 能力列表：5h/周窗口 + 月度剩余 credits（余额能力）。</summary>
    internal static MethodQueryResult BuildResult(CommandCodeUsageClient.AccountUsage usage,
        CredentialScope scope, SourceIdentity source)
    {
        var capabilities = new List<CapabilityValue>();
        AddWindow(capabilities, usage.Credits?.Limits?.FiveHour, "commandcode.fiveHour", "5h 窗口", scope, source);
        AddWindow(capabilities, usage.Credits?.Limits?.Weekly, "commandcode.weekly", "周窗口", scope, source);
        AddMonthlyBalance(capabilities, usage.Credits?.MonthlyRemaining, scope, source);

        return new MethodQueryResult(capabilities,
            capabilities.Count > 0 ? SnapshotStatus.Success : SnapshotStatus.NoData,
            capabilities.Count > 0 ? null : new FailureInfo(CandidateStatus.NoReliableUsage, "未解析到窗口/额度能力", DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
    }

    private static void AddWindow(List<CapabilityValue> list, CommandCodeUsageClient.WindowLimit? w, string key, string name,
        CredentialScope scope, SourceIdentity source)
    {
        if (w is null || w.Limit is not { } limit || limit <= 0) return;
        list.Add(new RollingWindowValue(
            CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
            Confidence: 1.0, IsPrivate: true, IsEstimated: false,
            WindowKey: key, WindowName: name, Status: "正常",
            Used: w.UsedMicroCents, Limit: w.LimitMicroCents, Remaining: w.RemainingMicroCents,
            Percent: w.Percent, ResetsAt: w.ResetAt, Unit: "microCents"));
    }

    /// <summary>月度剩余 credits：服务端只有剩余值（无上限/已用），按余额能力展示，不告警、不推导月度百分比。</summary>
    private static void AddMonthlyBalance(List<CapabilityValue> list, double? monthlyRemaining,
        CredentialScope scope, SourceIdentity source)
    {
        if (monthlyRemaining is not { } remaining) return;
        list.Add(new BalanceQuotaValue(
            CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
            Confidence: 1.0, IsPrivate: true, IsEstimated: false,
            Balance: (decimal)remaining, Used: null, Limit: null, Remaining: null,
            Currency: "USD", Unit: "credits", ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15)));
    }

    /// <summary>key 优先级：页面凭据 → 本地 CLI 登录 apiKey（本地凭据发现，不复制到页面配置）。</summary>
    private static string? ResolveKey(PageConfigRecord page)
    {
        var pageKey = page.CredentialRef.Target is { } target
            && CredentialStore.TryReadSecret(target, out var k) && !string.IsNullOrWhiteSpace(k)
            ? k : null;
        return pageKey ?? CommandCodeUsageClient.ReadLocalApiKey();
    }
}
