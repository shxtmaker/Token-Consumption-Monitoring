using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// 首期方法目录中尚未接入真实端点的条目：登记 descriptor 并作为凭据门控方法参与扫描。
/// 缺少匹配凭据时生成可解释的 AuthRequired 状态（不发网络请求）；凭据匹配但端点未接入时返回 Unsupported。
/// 这样目录完整可见、凭据边界可测试，且不会把未接入的方法冒充为可用。
/// </summary>
public sealed class CredentialGateMethod : IQueryMethod
{
    private readonly QueryMethodDescriptor _descriptor;
    private readonly string _providerHint;

    public CredentialGateMethod(QueryMethodDescriptor descriptor, string? providerHint = null)
    {
        _descriptor = descriptor;
        _providerHint = providerHint ?? descriptor.CredentialClass.ToString();
    }

    public QueryMethodDescriptor Describe() => _descriptor;

    public Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        if (_descriptor.CredentialClass is CredentialClass.LocalRecord or CredentialClass.None)
        {
            // 本地记录条目的端点/记录路径尚未接入：定义为不支持，避免冒充可用来源
            return Task.FromResult(MethodSupport.NotAvailable(_descriptor, CandidateStatus.Unsupported,
                $"方法 {_descriptor.MethodId} 尚未接入版本 {MethodSupport.ImplementationVersion}"));
        }
        if (context.Credentials.DeclaredClass != _descriptor.CredentialClass)
            return Task.FromResult(MethodSupport.AuthRequired(_descriptor,
                $"需要 {_descriptor.CredentialClass} 凭据；当前页面声明 {context.Credentials.DeclaredClass}",
                DetectionEvidence.Auth($"凭据类别不匹配：{_descriptor.CredentialClass}")));

        return Task.FromResult(MethodSupport.NotAvailable(_descriptor, CandidateStatus.Unsupported,
            $"方法 {_descriptor.MethodId} 的端点探测尚未接入",
            evidence: new[] { DetectionEvidence.Field("未实现：需要官方 endpoint 复核") }));
    }

    public Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
        => Task.FromResult(MethodQueryResult.Empty(SnapshotStatus.PermanentFailure, "方法未接入，无法查询"));
}

/// <summary>首期目录：目录可以列出全部已注册方法，但仅已接入的方法能进入可用候选。</summary>
public static class FirstWaveCatalog
{
    private static IReadOnlyList<CapabilityKind> C(params CapabilityKind[] kinds) => QueryMethodDescriptor.CapabilitiesOf(kinds);

    public static IQueryMethod[] Build()
    {
        var v = "1.0.0";
        return new IQueryMethod[]
        {
            // 远程官方（凭据门控，待接入）
            new CredentialGateMethod(new QueryMethodDescriptor("openrouter.key-usage.api-key", SourceKind.RollingWindowSnapshot, CredentialClass.ApiKey, C(CapabilityKind.RollingWindow, CapabilityKind.BalanceOrQuota), SourceStability.OfficialStable, MethodEnablement.Always, 10, v), "OpenRouter"),
            new CredentialGateMethod(new QueryMethodDescriptor("openrouter.management-credits.management-key", SourceKind.AllowanceOrBalance, CredentialClass.ManagementKey, C(CapabilityKind.BalanceOrQuota), SourceStability.OfficialStable, MethodEnablement.Conditional, 11, v), "OpenRouter"),
            new CredentialGateMethod(new QueryMethodDescriptor("openrouter.management-activity.management-key", SourceKind.RemoteOfficialStats, CredentialClass.ManagementKey, C(CapabilityKind.ReportedUsage, CapabilityKind.ReportedCost), SourceStability.OfficialStable, MethodEnablement.Conditional, 12, v), "OpenRouter"),

            new CredentialGateMethod(new QueryMethodDescriptor("openai.admin-usage.admin-key", SourceKind.RemoteOfficialStats, CredentialClass.AdminKey, C(CapabilityKind.ReportedUsage), SourceStability.OfficialStable, MethodEnablement.Conditional, 21, v), "OpenAI"),
            new CredentialGateMethod(new QueryMethodDescriptor("openai.admin-cost.admin-key", SourceKind.RemoteOfficialStats, CredentialClass.AdminKey, C(CapabilityKind.ReportedCost), SourceStability.OfficialStable, MethodEnablement.Conditional, 22, v), "OpenAI"),

            new CredentialGateMethod(new QueryMethodDescriptor("anthropic.admin-usage.admin-key", SourceKind.RemoteOfficialStats, CredentialClass.AdminKey, C(CapabilityKind.ReportedUsage), SourceStability.OfficialStable, MethodEnablement.Conditional, 23, v), "Anthropic"),
            new CredentialGateMethod(new QueryMethodDescriptor("anthropic.admin-cost.admin-key", SourceKind.RemoteOfficialStats, CredentialClass.AdminKey, C(CapabilityKind.ReportedCost), SourceStability.OfficialStable, MethodEnablement.Conditional, 24, v), "Anthropic"),

            new CredentialGateMethod(new QueryMethodDescriptor("opencode.console-export.service-account", SourceKind.RemoteOfficialStats, CredentialClass.ServiceAccountKey, C(CapabilityKind.ReportedUsage, CapabilityKind.ReportedCost), SourceStability.OfficialStable, MethodEnablement.Conditional, 31, v), "OpenCode Console"),

            new CredentialGateMethod(new QueryMethodDescriptor("xai.management-usage.management-key", SourceKind.RemoteOfficialStats, CredentialClass.ManagementKey, C(CapabilityKind.ReportedUsage), SourceStability.OfficialStable, MethodEnablement.Conditional, 41, v), "xAI"),
            new CredentialGateMethod(new QueryMethodDescriptor("xai.management-balance.management-key", SourceKind.AllowanceOrBalance, CredentialClass.ManagementKey, C(CapabilityKind.BalanceOrQuota), SourceStability.OfficialStable, MethodEnablement.Conditional, 42, v), "xAI"),

            new CredentialGateMethod(new QueryMethodDescriptor("fireworks.quota.account", SourceKind.AllowanceOrBalance, CredentialClass.ApiKey, C(CapabilityKind.BalanceOrQuota), SourceStability.OfficialStable, MethodEnablement.Conditional, 51, v), "Fireworks"),
            new CredentialGateMethod(new QueryMethodDescriptor("fireworks.usage-cost.account-admin", SourceKind.RemoteOfficialStats, CredentialClass.ManagementKey, C(CapabilityKind.ReportedUsage, CapabilityKind.ReportedCost), SourceStability.OfficialStable, MethodEnablement.Conditional, 52, v), "Fireworks"),

            // 本地备选（记录路径尚未接入：LocalRecord 门控返回 Unsupported，避免冒充可用来源）
            new CredentialGateMethod(new QueryMethodDescriptor("local.opencode.stats", SourceKind.LocalRecord, CredentialClass.LocalRecord, C(CapabilityKind.ReportedUsage, CapabilityKind.ReportedCost), SourceStability.LocalFallback, MethodEnablement.Always, 61, v), "OpenCode CLI"),
            new CredentialGateMethod(new QueryMethodDescriptor("local.claude-code.telemetry", SourceKind.LocalRecord, CredentialClass.LocalRecord, C(CapabilityKind.ReportedUsage, CapabilityKind.ResponseUsage), SourceStability.LocalFallback, MethodEnablement.Always, 62, v), "Claude Code"),
            new CredentialGateMethod(new QueryMethodDescriptor("local.codex.rollout", SourceKind.LocalRecord, CredentialClass.LocalRecord, C(CapabilityKind.ReportedUsage, CapabilityKind.RollingWindow), SourceStability.LocalFallback, MethodEnablement.Always, 63, v), "Codex"),
            new CredentialGateMethod(new QueryMethodDescriptor("local.gemini.session", SourceKind.LocalRecord, CredentialClass.LocalRecord, C(CapabilityKind.ReportedUsage), SourceStability.LocalFallback, MethodEnablement.Always, 64, v), "Gemini"),
        };
    }
}
