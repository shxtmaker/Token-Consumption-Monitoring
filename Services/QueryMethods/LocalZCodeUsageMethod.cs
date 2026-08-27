using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// local.zcode.usage：ZCode CLI 本地 SQLite 记录（model_usage），本地备选来源。
/// ZCode 只是本地记录之一：注册表扫描所有可用本地方法，不强制检测 ZCode。
/// 归属依据为 provider.apiKey 或 baseURL 与页面匹配；无法确认归属的记录不自动挂页。
/// </summary>
public sealed class LocalZCodeUsageMethod : IQueryMethod
{
    private static readonly QueryMethodDescriptor Descriptor = new(
        "local.zcode.usage",
        SourceKind.LocalRecord,
        CredentialClass.LocalRecord,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ReportedUsage),
        SourceStability.LocalFallback,
        MethodEnablement.Always,
        DefaultPriority: 60,
        MethodSupport.ImplementationVersion);

    private const string Provider = "zcode";
    private readonly ZCodeUsageService _zcode;

    public LocalZCodeUsageMethod(ZCodeUsageService zcode) => _zcode = zcode;

    public QueryMethodDescriptor Describe() => Descriptor;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        if (!_zcode.DatabaseExists)
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NoReliableUsage,
                "本机无 zcode 记录（本地回退来源）",
                evidence: new[] { DetectionEvidence.LocalSchema("~/.zcode/cli/db/db.sqlite 不存在") });

        var (schemaOk, schemaError) = await _zcode.TryVerifySchemaAsync(ct);
        if (!schemaOk)
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.SchemaMismatch, schemaError,
                evidence: new[] { DetectionEvidence.LocalSchema(schemaError) });

        if (!IsAttributable(page))
            return MethodSupport.NotAvailable(Descriptor, CandidateStatus.NoReliableUsage,
                "本机存在 zcode 记录但无法确认归属（未关联页面）",
                evidence: new[] { DetectionEvidence.LocalSchema("provider 归属不匹配页面 key/域名") });

        return MethodSupport.Available(Descriptor, new CredentialScope(CredentialClass.LocalRecord, Provider),
            new Coverage(DateTime.Today, DateTime.Today.AddDays(1), Granularity.PerDay),
            new[] { DetectionEvidence.LocalSchema("model_usage 可读且归属匹配") },
            source: new SourceIdentity(Provider, "local", Descriptor.MethodId, "~/.zcode/cli/db/db.sqlite"),
            confidence: 70);
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        var byProvider = await _zcode.ComputeTodayByProviderAsync(ct);
        var scope = candidate.CredentialScope ?? new CredentialScope(CredentialClass.LocalRecord, Provider);
        var source = candidate.Source ?? new SourceIdentity(Provider, "local", Descriptor.MethodId, "~/.zcode/cli/db/db.sqlite");

        long total = 0;
        var matchedProvider = false;
        long? requestCount = null;
        var rows = new List<ModelUsageRow>();
        foreach (var pu in byProvider)
        {
            if (!Belongs(pu.Provider, page)) continue;
            matchedProvider = true;
            foreach (var m in pu.Models)
            {
                total += m.Tokens;
                rows.Add(new ModelUsageRow($"{pu.Provider.Name} · {m.Model}", m.Tokens));
            }
        }

        if (!matchedProvider)
        {
            return new MethodQueryResult(Array.Empty<CapabilityValue>(), SnapshotStatus.NoData,
                new FailureInfo(CandidateStatus.NoReliableUsage, "未找到可归属的本地记录", DateTimeOffset.UtcNow),
                DateTimeOffset.UtcNow);
        }

        var capability = new ReportedUsageValue(
            CapabilityKind.ReportedUsage, source, scope,
            new Coverage(DateTime.Today, DateTime.Today.AddDays(1), Granularity.PerDay), DateTimeOffset.UtcNow,
            Confidence: 0.9, IsPrivate: false, IsEstimated: false,
            TotalTokens: total, TotalRequests: requestCount ?? 0, Models: rows,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1));
        return new MethodQueryResult(new CapabilityValue[] { capability }, SnapshotStatus.Success, null, DateTimeOffset.UtcNow);
    }

    private bool IsAttributable(PageConfigRecord page)
        => _zcode.GetProviders().Any(p => Belongs(p, page));

    /// <summary>
    /// 归属规则：有 key 页面按 provider.apiKey 精确匹配；无 key 页面（控制台会话）按 baseURL 主域匹配。
    /// </summary>
    private static bool Belongs(ZCodeUsageService.ProviderInfo provider, PageConfigRecord page)
    {
        if (page.ParseProtocol() == KeyFormat.Protocol.DeepSeekConsole || page.CredentialRef.ResolveClass() == CredentialClass.ConsoleSession)
        {
            var domain = DomainOf(page.BaseUrl);
            return domain is not null && DomainOf(provider.BaseUrl) == domain;
        }
        return provider.ApiKey is { } pk && CredentialStore.TryReadSecret(page.CredentialRef.Target!, out var key)
               && string.Equals(pk, key, StringComparison.Ordinal);
    }

    /// <summary>取 URL 主域（末两段，如 platform.deepseek.com → deepseek.com）。</summary>
    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var parts = u.Host.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : u.Host;
    }
}
