using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

/// <summary>Phase 3：候选排序、选择与并列处理（不依赖套餐名称）。</summary>
public class CandidateSelectionTests
{
    private static readonly QueryMethodDescriptor RemoteStats = new(
        "openai.admin-usage.admin-key", SourceKind.RemoteOfficialStats, CredentialClass.AdminKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ReportedUsage), SourceStability.OfficialStable, MethodEnablement.Always, 21, "1.0.0");

    private static readonly QueryMethodDescriptor Balance = new(
        "deepseek.balance.api-key", SourceKind.AllowanceOrBalance, CredentialClass.ApiKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota), SourceStability.OfficialStable, MethodEnablement.Always, 40, "1.0.0");

    private static readonly QueryMethodDescriptor LocalRecord = new(
        "local.zcode.usage", SourceKind.LocalRecord, CredentialClass.LocalRecord,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ReportedUsage), SourceStability.LocalFallback, MethodEnablement.Always, 60, "1.0.0");

    private static readonly QueryMethodDescriptor ConsolePrivate = new(
        "deepseek.console-usage.compat", SourceKind.ConsoleOrPrivateUI, CredentialClass.ConsoleSession,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ReportedUsage, CapabilityKind.ReportedCost), SourceStability.PrivateCompat, MethodEnablement.PrivateCompatOnly, 50, "1.0.0");

    private static readonly QueryMethodDescriptor Probe = new(
        "endpoint.probe", SourceKind.Probe, CredentialClass.ApiKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ProbeDiagnostic), SourceStability.ProbeOnly, MethodEnablement.Always, 100, "1.0.0");

    private static MethodCandidate Available(QueryMethodDescriptor d, int confidence = 90, string? scope = null) =>
        MethodSupport.Available(d, new CredentialScope(d.CredentialClass, scope), Coverage.Unknown, Array.Empty<DetectionEvidence>());

    private static MethodCandidate Auth(QueryMethodDescriptor d) =>
        MethodSupport.AuthRequired(d, "缺凭据");

    [Fact]
    public void Order_PrefersRemoteOfficialStats_ThenBalance_ThenLocal_ThenProbe()
    {
        var ordered = CandidateSelector.Order(new[]
        {
            Probe, Balance, LocalRecord, RemoteStats,
            ConsolePrivate,
        }.Select(d => Available(d)));

        Assert.Equal(RemoteStats.MethodId, ordered[0].Method.MethodId);
        Assert.Equal(Balance.MethodId, ordered[1].Method.MethodId);
        Assert.Equal(LocalRecord.MethodId, ordered[2].Method.MethodId);
        Assert.Equal(ConsolePrivate.MethodId, ordered[3].Method.MethodId);
        Assert.Equal(Probe.MethodId, ordered[4].Method.MethodId);
    }

    [Fact]
    public void Select_SingleHighestAvailable_IsChosen()
    {
        var ordered = CandidateSelector.Order(new[]
        {
            Available(Probe, 50), Available(LocalRecord, 70),
        });

        var selection = CandidateSelector.Select(ordered);
        Assert.Equal(LocalRecord.MethodId, selection.SelectedMethodId);
        Assert.Equal(CandidateStatus.Available, selection.Status);
    }

    [Fact]
    public void Select_TiedHighest_RequiresSelection_NoRandomChoice()
    {
        // 同 source kind、同优先级、同置信度的两个候选 → 并列
        var a = Available(RemoteStats, 90);
        var b = Available(RemoteStats, 90);
        var ordered = CandidateSelector.Order(new[] { a, b });

        var selection = CandidateSelector.Select(ordered);
        Assert.Null(selection.SelectedMethodId);
        Assert.Equal(CandidateStatus.RequiresSelection, selection.Status);
    }

    [Fact]
    public void Select_OnlyAuthRequired_ReportsAuthRequired()
    {
        var ordered = CandidateSelector.Order(new[] { Auth(Balance), Auth(Probe) });
        var selection = CandidateSelector.Select(ordered);

        Assert.Null(selection.SelectedMethodId);
        Assert.Equal(CandidateStatus.AuthRequired, selection.Status);
    }

    [Fact]
    public void Select_NoAvailableNoAuth_ReportsNoReliableUsage()
    {
        var ordered = CandidateSelector.Order(new[]
        {
            MethodSupport.NotAvailable(Probe, CandidateStatus.Unsupported, "未接入"),
        });
        var selection = CandidateSelector.Select(ordered);

        Assert.Null(selection.SelectedMethodId);
        Assert.Equal(CandidateStatus.Unsupported, selection.Status);
    }

    [Fact]
    public void MethodSupport_Available_CarriesSourceScopeAndEvidence()
    {
        var scope = new CredentialScope(CredentialClass.ApiKey, "opencode");
        var c = MethodSupport.Available(Balance, scope, Coverage.Unknown,
            new[] { DetectionEvidence.Field("/user/balance") },
            source: new SourceIdentity("deepseek", "api-key", Balance.MethodId, "https://api.deepseek.com/user/balance"),
            confidence: 85);

        Assert.True(c.IsAvailable);
        Assert.Equal(85, c.Confidence);
        Assert.Equal("deepseek", c.Source!.Provider);
        Assert.Single(c.Evidence);
    }
}
