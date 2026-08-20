using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

/// <summary>Phase 3：协调器编排（扫描 → 选择 → 查询 / 轮询不重扫 / 按能力回退 / 临时覆盖）。所有方法为桩实现。</summary>
public class RuntimeCoordinatorTests
{
    private static readonly QueryMethodDescriptor BalanceMethod = new(
        "deepseek.balance.api-key", SourceKind.AllowanceOrBalance, CredentialClass.ApiKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota), SourceStability.OfficialStable, MethodEnablement.Always, 40, "1.0.0");

    private static readonly QueryMethodDescriptor LocalMethod = new(
        "local.zcode.usage", SourceKind.LocalRecord, CredentialClass.LocalRecord,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ReportedUsage), SourceStability.LocalFallback, MethodEnablement.Always, 60, "1.0.0");

    private static readonly QueryMethodDescriptor ProbeMethod = new(
        "endpoint.probe", SourceKind.Probe, CredentialClass.ApiKey,
        QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.ProbeDiagnostic), SourceStability.ProbeOnly, MethodEnablement.Always, 100, "1.0.0");

    private sealed class StubMethod : IQueryMethod
    {
        private readonly QueryMethodDescriptor _d;
        private readonly MethodCandidate _scan;
        private readonly MethodQueryResult _query;
        public int ScanCalls { get; private set; }
        public int QueryCalls { get; private set; }

        public StubMethod(QueryMethodDescriptor d, MethodCandidate scan, MethodQueryResult query)
        {
            _d = d;
            _scan = scan;
            _query = query;
        }

        public QueryMethodDescriptor Describe() => _d;
        public Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
        { ScanCalls++; return Task.FromResult(_scan); }
        public Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
        { QueryCalls++; return Task.FromResult(_query); }
    }

    private static BalanceQuotaValue BalanceValue(string pageId) => new(
        CapabilityKind.BalanceOrQuota,
        new SourceIdentity("deepseek", "api-key", BalanceMethod.MethodId, "https://api.deepseek.com/user/balance"),
        new CredentialScope(CredentialClass.ApiKey, "deepseek"), Coverage.Unknown, DateTimeOffset.UtcNow,
        Confidence: 1.0, IsPrivate: false, IsEstimated: false, Balance: 9.9m, Used: null, Limit: null, Remaining: null,
        Currency: "CNY", Unit: null);

    private static PageConfigRecord Page() => new()
    {
        Id = "page-1",
        Name = "DeepSeek",
        BaseUrl = "https://api.deepseek.com",
        Protocol = "ChatCompletions",
        CredentialRef = CredentialReference.None,
    };

    private static (PageRuntimeCoordinator Coordinator, string TempDir) NewCoordinator(params IQueryMethod[] methods)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "tcm_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var coordinator = new PageRuntimeCoordinator(
            new QueryMethodRegistry(methods),
            new FingerprintBuilder(methods.Select(m => m.Describe().ImplementationVersion).Append("1.0.0")),
            new MethodStateStore(tempDir),
            new MethodResultCache(),
            new ZCodeUsageService());
        return (coordinator, tempDir);
    }

    private static MethodCandidate Avail(QueryMethodDescriptor d) =>
        MethodSupport.Available(d, new CredentialScope(d.CredentialClass), Coverage.Unknown, Array.Empty<DetectionEvidence>());

    [Fact]
    public async Task ManualRefresh_ScansSelectsAndProducesSnapshot()
    {
        var balance = new StubMethod(BalanceMethod, Avail(BalanceMethod),
            new MethodQueryResult(new CapabilityValue[] { BalanceValue("page-1") }, SnapshotStatus.Success, null, DateTimeOffset.UtcNow));
        var probe = new StubMethod(ProbeMethod, Avail(ProbeMethod),
            new MethodQueryResult(Array.Empty<CapabilityValue>(), SnapshotStatus.ProbeOnly,
                new FailureInfo(CandidateStatus.NoReliableUsage, "probe", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));

        var (coordinator, tempDir) = NewCoordinator(balance, probe);
        try
        {
            var result = await coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);

            Assert.NotNull(result.Scan);
            Assert.Equal(BalanceMethod.MethodId, result.Scan!.SelectedMethodId);
            Assert.Equal(1, balance.ScanCalls);
            Assert.Equal(1, balance.QueryCalls);
            Assert.Equal(SnapshotStatus.Success, result.Snapshot.Status);
            Assert.Single(result.Snapshot.Balances);
            Assert.Equal(9.9m, result.Snapshot.Balances.Single().Balance);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Poll_AfterScan_DoesNotRescan()
    {
        var balance = new StubMethod(BalanceMethod, Avail(BalanceMethod),
            new MethodQueryResult(new CapabilityValue[] { BalanceValue("page-1") }, SnapshotStatus.Success, null, DateTimeOffset.UtcNow));

        var (coordinator, tempDir) = NewCoordinator(balance);
        try
        {
            var first = await coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            Assert.NotNull(first.Scan);
            Assert.Equal(1, balance.ScanCalls);

            var second = await coordinator.RefreshAsync(Page(), RefreshReason.Poll, CancellationToken.None);
            Assert.Null(second.Scan);                    // 轮询不重扫候选
            Assert.Equal(1, balance.ScanCalls);          // 未再扫描
            Assert.Single(second.Snapshot.Balances);     // 快照来自缓存
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CapabilityFallback_FillsMissingCapabilityFromNextSource_NoSum()
    {
        var balance = new StubMethod(BalanceMethod, Avail(BalanceMethod),
            new MethodQueryResult(new CapabilityValue[] { BalanceValue("page-1") }, SnapshotStatus.Success, null, DateTimeOffset.UtcNow));
        var local = new StubMethod(LocalMethod, Avail(LocalMethod),
            new MethodQueryResult(new CapabilityValue[]
            {
                new ReportedUsageValue(CapabilityKind.ReportedUsage,
                    new SourceIdentity("zcode", "local", LocalMethod.MethodId, "~/.zcode/cli/db/db.sqlite"),
                    new CredentialScope(CredentialClass.LocalRecord, "zcode"), new Coverage(DateTime.Today, DateTime.Today.AddDays(1), Granularity.PerDay),
                    DateTimeOffset.UtcNow, Confidence: 0.9, IsPrivate: true, IsEstimated: false,
                    TotalTokens: 5000, TotalRequests: 0, Models: new[] { new ModelUsageRow("deepseek-v4", 5000) }),
            }, SnapshotStatus.Success, null, DateTimeOffset.UtcNow));

        var (coordinator, tempDir) = NewCoordinator(balance, local);
        try
        {
            var result = await coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);

            // 主方法（余额）不提供 ReportedUsage → 本地记录回退补齐；同一能力不合并
            Assert.Single(result.Snapshot.Balances);
            Assert.Single(result.Snapshot.ReportedUsages);
            Assert.Equal(5000, result.Snapshot.ReportedUsages.Single().TotalTokens);
            Assert.True(local.QueryCalls >= 1);   // 回退来源确实被查询过
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryOverride_AppliesThenResetsOnClear()
    {
        var balance = new StubMethod(BalanceMethod, Avail(BalanceMethod),
            new MethodQueryResult(new CapabilityValue[] { BalanceValue("page-1") }, SnapshotStatus.Success, null, DateTimeOffset.UtcNow));
        var probe = new StubMethod(ProbeMethod, Avail(ProbeMethod),
            new MethodQueryResult(new CapabilityValue[]
            {
                new ProbeDiagnosticValue(CapabilityKind.ProbeDiagnostic,
                    new SourceIdentity("probe", "endpoint", ProbeMethod.MethodId, "https://api.deepseek.com/models"),
                    new CredentialScope(CredentialClass.None), Coverage.Unknown, DateTimeOffset.UtcNow,
                    Confidence: 0.9, IsPrivate: false, IsEstimated: false, Connected: true, Authenticated: true,
                    Models: Array.Empty<string>(), Diagnostic: null),
            }, SnapshotStatus.ProbeOnly, null, DateTimeOffset.UtcNow));

        var (coordinator, tempDir) = NewCoordinator(balance, probe);
        try
        {
            coordinator.SetTemporaryOverride("page-1", ProbeMethod.MethodId);
            var overridden = await coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            Assert.Equal(ProbeMethod.MethodId, overridden.Snapshot.Metadata.SelectedMethodId);

            coordinator.SetTemporaryOverride("page-1", null);
            var reset = await coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            Assert.Equal(BalanceMethod.MethodId, reset.Snapshot.Metadata.SelectedMethodId);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
