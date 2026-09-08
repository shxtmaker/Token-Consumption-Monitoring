using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI.Diagnostics;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class ScanDiagnosticsViewModelTests
{
    private static readonly PageConfigRecord Page = new()
    {
        Id = "diagnostics-page",
        Name = "test",
        BaseUrl = "https://example.invalid",
        Protocol = "ChatCompletions",
        CredentialRef = CredentialReference.None,
    };

    private static MethodCandidate Candidate(string id, CandidateStatus status, int priority = 10)
    {
        var descriptor = new QueryMethodDescriptor(
            id, SourceKind.AllowanceOrBalance, CredentialClass.None,
            QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
            SourceStability.OfficialStable, MethodEnablement.Always, priority, "1.0.0");
        return status == CandidateStatus.Available
            ? MethodSupport.Available(descriptor,
                new CredentialScope(CredentialClass.None, "test"), Coverage.Unknown,
                Array.Empty<DetectionEvidence>(),
                new SourceIdentity("test", "account", id, "https://example.invalid"))
            : MethodSupport.NotAvailable(descriptor, status, "scan unavailable");
    }

    private static ScanReport Report(params MethodCandidate[] candidates) => new(
        Page.Id, "test-fingerprint", candidates,
        CapabilitySourcePlan.Build(candidates), DateTimeOffset.UtcNow);

    [Fact]
    public void InitialScan_ShowsOnlyAvailableCandidatesInReportOrderWithCurrentMarkers()
    {
        var report = Report(
            Candidate("source-z", CandidateStatus.Available, 10),
            Candidate("auth", CandidateStatus.AuthRequired),
            Candidate("source-a", CandidateStatus.Available, 20),
            Candidate("network", CandidateStatus.NetworkFailure),
            Candidate("source-b", CandidateStatus.Available, 30));
        var viewModel = new ScanDiagnosticsViewModel();

        viewModel.Update(Page, report, "source-b");

        Assert.Collection(viewModel.Candidates,
            candidate =>
            {
                Assert.Equal("source-z", candidate.MethodId);
                Assert.True(candidate.IsCurrent);
            },
            candidate =>
            {
                Assert.Equal("source-a", candidate.MethodId);
                Assert.False(candidate.IsCurrent);
            },
            candidate =>
            {
                Assert.Equal("source-b", candidate.MethodId);
                Assert.True(candidate.IsCurrent);
            });
        Assert.All(viewModel.Candidates, candidate => Assert.True(candidate.IsAvailable));
        Assert.True(viewModel.HasCandidates);
        Assert.Equal("3 个可用", viewModel.CandidateCountLabel);
        Assert.Equal(5, report.Candidates.Count);
    }

    [Fact]
    public void Rescan_ReplacesCandidatesWhenAvailabilityChangesInBothDirections()
    {
        var viewModel = new ScanDiagnosticsViewModel();
        viewModel.Update(Page, Report(
            Candidate("previous", CandidateStatus.Available),
            Candidate("recovered", CandidateStatus.NetworkFailure)), null);
        Assert.Equal("previous", Assert.Single(viewModel.Candidates).MethodId);

        viewModel.Update(Page, Report(
            Candidate("previous", CandidateStatus.Forbidden),
            Candidate("recovered", CandidateStatus.Available)), null);

        var current = Assert.Single(viewModel.Candidates);
        Assert.Equal("recovered", current.MethodId);
        Assert.True(current.IsCurrent);
        Assert.True(viewModel.HasCandidates);
        Assert.Equal("1 个可用", viewModel.CandidateCountLabel);
    }

    [Fact]
    public void AllUnavailable_ClearsPreviousCandidatesAndPreservesAuthenticationPrompt()
    {
        var viewModel = new ScanDiagnosticsViewModel();
        viewModel.Update(Page, Report(Candidate("previous", CandidateStatus.Available)), null);
        var report = Report(
            Candidate("previous", CandidateStatus.AuthRequired),
            Candidate("network", CandidateStatus.NetworkFailure));

        viewModel.Update(Page, report, "previous");

        Assert.Empty(viewModel.Candidates);
        Assert.False(viewModel.HasCandidates);
        Assert.Equal("0 个可用", viewModel.CandidateCountLabel);
        Assert.Equal("需要凭据/权限", viewModel.SelectionLabel);
        Assert.Equal(CandidateStatus.AuthRequired, report.SelectionStatus);
        Assert.Equal(2, report.Candidates.Count);
    }

    [Fact]
    public void AvailableCandidatesRemainVisibleWhenTheirSelectionIsTied()
    {
        var report = Report(
            Candidate("source-a", CandidateStatus.Available),
            Candidate("source-b", CandidateStatus.Available));
        var viewModel = new ScanDiagnosticsViewModel();

        viewModel.Update(Page, report, null);

        Assert.True(viewModel.RequiresSelection);
        Assert.Equal(new[] { "source-a", "source-b" },
            viewModel.Candidates.Select(candidate => candidate.MethodId));
        Assert.All(viewModel.Candidates, candidate =>
        {
            Assert.True(candidate.IsAvailable);
            Assert.False(candidate.IsCurrent);
        });
    }

    public static IEnumerable<object[]> UnavailableStatuses =>
        Enum.GetValues<CandidateStatus>()
            .Where(status => status != CandidateStatus.Available)
            .Select(status => new object[] { status });

    [Theory]
    [MemberData(nameof(UnavailableStatuses))]
    public void NonAvailableStatus_IsNeverListed(CandidateStatus status)
    {
        var viewModel = new ScanDiagnosticsViewModel();

        viewModel.Update(Page, Report(Candidate("unavailable", status)), "unavailable");

        Assert.Empty(viewModel.Candidates);
        Assert.False(viewModel.HasCandidates);
        Assert.Equal("0 个可用", viewModel.CandidateCountLabel);
    }
}
