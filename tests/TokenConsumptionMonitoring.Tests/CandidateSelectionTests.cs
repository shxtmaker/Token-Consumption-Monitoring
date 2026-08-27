using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class CandidateSelectionTests
{
    private static MethodCandidate Candidate(
        string id,
        SourceKind sourceKind,
        SourceStability stability,
        int priority,
        CapabilityKind capability,
        int confidence = 80)
    {
        var descriptor = new QueryMethodDescriptor(
            id,
            sourceKind,
            CredentialClass.ApiKey,
            QueryMethodDescriptor.CapabilitiesOf(capability),
            stability,
            MethodEnablement.Always,
            priority,
            "1.0.0");
        return MethodSupport.Available(
            descriptor,
            new CredentialScope(CredentialClass.ApiKey, "test"),
            Coverage.Unknown,
            Array.Empty<DetectionEvidence>(),
            new SourceIdentity("test", "account", id, "https://test.invalid"),
            confidence);
    }

    [Fact]
    public void CapabilityPlan_SelectsOneSourcePerCapability()
    {
        var official = Candidate("official.balance", SourceKind.AllowanceOrBalance,
            SourceStability.OfficialStable, 10, CapabilityKind.BalanceOrQuota);
        var local = Candidate("local.balance", SourceKind.LocalRecord,
            SourceStability.LocalFallback, 20, CapabilityKind.BalanceOrQuota);

        var plan = CapabilitySourcePlan.Build(new[] { local, official });

        var selected = Assert.Single(plan.SelectedByCapability);
        Assert.Equal(CapabilityKind.BalanceOrQuota, selected.Key);
        Assert.Equal("official.balance", selected.Value.Method.MethodId);
    }

    [Fact]
    public void CapabilityPlan_TracksTieAsRequiresSelection()
    {
        var first = Candidate("source-a", SourceKind.AllowanceOrBalance,
            SourceStability.OfficialStable, 10, CapabilityKind.BalanceOrQuota);
        var second = Candidate("source-b", SourceKind.AllowanceOrBalance,
            SourceStability.OfficialStable, 10, CapabilityKind.BalanceOrQuota);

        var plan = CapabilitySourcePlan.Build(new[] { first, second });

        Assert.True(plan.RequiresSelection);
        Assert.False(plan.TryGet(CapabilityKind.BalanceOrQuota, out _));
    }

    [Fact]
    public void CapabilityPlan_CanSelectDifferentSourcesForDifferentCapabilities()
    {
        var balance = Candidate("balance", SourceKind.AllowanceOrBalance,
            SourceStability.OfficialStable, 10, CapabilityKind.BalanceOrQuota);
        var usage = Candidate("usage", SourceKind.RemoteOfficialStats,
            SourceStability.OfficialStable, 20, CapabilityKind.ReportedUsage);

        var plan = CapabilitySourcePlan.Build(new[] { balance, usage });

        Assert.Equal("balance", plan.SelectedMethodIds[CapabilityKind.BalanceOrQuota]);
        Assert.Equal("usage", plan.SelectedMethodIds[CapabilityKind.ReportedUsage]);
        Assert.Equal(2, plan.SelectedCandidates.Count);
    }
}
