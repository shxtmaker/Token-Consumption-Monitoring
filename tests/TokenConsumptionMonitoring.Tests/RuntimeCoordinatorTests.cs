using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class RuntimeCoordinatorTests
{
    private sealed class StubMethod : IQueryMethod
    {
        private readonly QueryMethodDescriptor _descriptor;
        private readonly MethodCandidate _candidate;
        private readonly Func<int, MethodQueryResult> _result;

        public int ScanCalls { get; private set; }
        public int QueryCalls { get; private set; }

        public StubMethod(QueryMethodDescriptor descriptor, Func<int, MethodQueryResult> result, MethodCandidate? candidate = null)
        {
            _descriptor = descriptor;
            _candidate = candidate ?? MethodSupport.Available(
                descriptor,
                new CredentialScope(descriptor.CredentialClass, "test"),
                Coverage.Unknown,
                Array.Empty<DetectionEvidence>(),
                new SourceIdentity("test", "account", descriptor.MethodId, "https://test.invalid/usage"));
            _result = result;
        }

        public QueryMethodDescriptor Describe() => _descriptor;

        public Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
        {
            ScanCalls++;
            return Task.FromResult(_candidate);
        }

        public Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
        {
            QueryCalls++;
            return Task.FromResult(_result(QueryCalls));
        }
    }

    private static QueryMethodDescriptor Descriptor(
        string id,
        CapabilityKind capability,
        SourceKind source = SourceKind.AllowanceOrBalance,
        SourceStability stability = SourceStability.OfficialStable,
        int priority = 10)
        => new(id, source, CredentialClass.None,
            QueryMethodDescriptor.CapabilitiesOf(capability), stability, MethodEnablement.Always, priority, "1.0.0");

    private static PageConfigRecord Page() => new()
    {
        Id = "page-1",
        Name = "test",
        BaseUrl = "https://example.invalid",
        Protocol = "ChatCompletions",
        CredentialRef = CredentialReference.None,
    };

    private static RollingWindowValue Window(string key, int percent, DateTimeOffset? fetchedAt = null) => new(
        CapabilityKind.RollingWindow,
        new SourceIdentity("test", "account", "windows", "https://test.invalid/usage"),
        new CredentialScope(CredentialClass.None, "test"),
        Coverage.Unknown,
        fetchedAt ?? DateTimeOffset.UtcNow,
        1,
        false,
        false,
        key,
        key,
        "ok",
        null,
        100,
        100 - percent,
        percent,
        DateTimeOffset.UtcNow.AddHours(1),
        "units");

    private static MethodQueryResult Success(params CapabilityValue[] values)
        => new(values, SnapshotStatus.Success, null, DateTimeOffset.UtcNow);

    private static MethodQueryResult Failure(CandidateStatus status = CandidateStatus.NetworkFailure)
        => new(Array.Empty<CapabilityValue>(), QueryFailureClassifier.SnapshotStatusOf(status),
            new FailureInfo(status, "simulated failure", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

    private static (PageRuntimeCoordinator Coordinator, MethodResultCache Cache, MethodStateStore StateStore, string Directory) NewCoordinator(
        params StubMethod[] methods)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_runtime_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stateStore = new MethodStateStore(directory);
        var cache = new MethodResultCache();
        var descriptors = methods.Select(method => method.Describe());
        var coordinator = new PageRuntimeCoordinator(
            new QueryMethodRegistry(methods),
            new FingerprintBuilder(descriptors),
            stateStore,
            cache,
            new ZCodeUsageService());
        return (coordinator, cache, stateStore, directory);
    }

    [Fact]
    public async Task OneMethodReturningThreeWindows_PreservesAllItems()
    {
        var descriptor = Descriptor("windows", CapabilityKind.RollingWindow, SourceKind.RollingWindowSnapshot);
        var method = new StubMethod(descriptor, _ => Success(
            Window("five_hour", 30), Window("weekly", 40), Window("monthly", 50)));
        var runtime = NewCoordinator(method);
        try
        {
            var result = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);

            Assert.Equal(3, result.Snapshot.Windows.Count());
            Assert.Equal(new[] { "five_hour", "weekly", "monthly" }, result.Snapshot.Windows.Select(w => w.WindowKey));
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task SameCapability_UsesSelectedSourceWithoutSummingOrQueryingOtherSource()
    {
        var officialDescriptor = Descriptor("official", CapabilityKind.BalanceOrQuota, priority: 10);
        var localDescriptor = Descriptor("local", CapabilityKind.BalanceOrQuota,
            SourceKind.LocalRecord, SourceStability.LocalFallback, 20);
        var official = new StubMethod(officialDescriptor, _ => Success(new BalanceQuotaValue(
            CapabilityKind.BalanceOrQuota,
            new SourceIdentity("official", "account", "official", "https://official.invalid"),
            new CredentialScope(CredentialClass.None, "official"), Coverage.Unknown, DateTimeOffset.UtcNow,
            1, false, false, 9m, null, null, null, "USD", "credits")));
        var local = new StubMethod(localDescriptor, _ => Success(new BalanceQuotaValue(
            CapabilityKind.BalanceOrQuota,
            new SourceIdentity("local", "account", "local", "https://local.invalid"),
            new CredentialScope(CredentialClass.None, "local"), Coverage.Unknown, DateTimeOffset.UtcNow,
            1, false, false, 3m, null, null, null, "USD", "credits")));
        var runtime = NewCoordinator(official, local);
        try
        {
            var result = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);

            var balance = Assert.Single(result.Snapshot.Balances);
            Assert.Equal(9m, balance.Balance);
            Assert.Equal(1, official.QueryCalls);
            Assert.Equal(0, local.QueryCalls);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task FailedSelectedSource_UsesCapabilityFallbackWithoutSumming()
    {
        var primaryDescriptor = Descriptor("primary", CapabilityKind.BalanceOrQuota, priority: 10);
        var fallbackDescriptor = Descriptor("fallback", CapabilityKind.BalanceOrQuota,
            SourceKind.LocalRecord, SourceStability.LocalFallback, 20);
        var primary = new StubMethod(primaryDescriptor, _ => Failure());
        var fallback = new StubMethod(fallbackDescriptor, _ => Success(new BalanceQuotaValue(
            CapabilityKind.BalanceOrQuota, new SourceIdentity("fallback", "a", "fallback", "https://a.invalid"),
            new CredentialScope(CredentialClass.LocalRecord), Coverage.Unknown, DateTimeOffset.UtcNow,
            1, false, false, 3m, null, null, 3m, "USD", "credits")));
        var runtime = NewCoordinator(primary, fallback);
        try
        {
            var result = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);

            var balance = Assert.Single(result.Snapshot.Balances);
            Assert.Equal(3m, balance.Balance);
            Assert.Equal(SnapshotStatus.SuccessPartial, result.Snapshot.Status);
            Assert.Equal("fallback", result.Snapshot.Metadata.SelectedMethodId);
            Assert.NotNull(result.Failure);
            Assert.Equal(RetryPolicy.MaxTransientRetries + 1, primary.QueryCalls);
            Assert.Equal(1, fallback.QueryCalls);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task DifferentCapabilities_SelectDifferentSources()
    {
        var balanceDescriptor = Descriptor("balance", CapabilityKind.BalanceOrQuota);
        var usageDescriptor = Descriptor("usage", CapabilityKind.ReportedUsage, SourceKind.RemoteOfficialStats, priority: 20);
        var balance = new StubMethod(balanceDescriptor, _ => Success(new BalanceQuotaValue(
            CapabilityKind.BalanceOrQuota, new SourceIdentity("balance", "a", "balance", "https://a.invalid"),
            new CredentialScope(CredentialClass.None), Coverage.Unknown, DateTimeOffset.UtcNow,
            1, false, false, 0m, null, null, null, "USD", null)));
        var usage = new StubMethod(usageDescriptor, _ => Success(new ReportedUsageValue(
            CapabilityKind.ReportedUsage, new SourceIdentity("usage", "a", "usage", "https://a.invalid"),
            new CredentialScope(CredentialClass.None), Coverage.Unknown, DateTimeOffset.UtcNow,
            1, false, false, 0, 0, Array.Empty<ModelUsageRow>())));
        var runtime = NewCoordinator(balance, usage);
        try
        {
            var result = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);

            Assert.Equal(2, result.Snapshot.Metadata.EffectiveSelectedMethodIds.Count);
            Assert.Single(result.Snapshot.Balances);
            Assert.Single(result.Snapshot.ReportedUsages);
            Assert.Equal(SnapshotStatus.Success, result.Snapshot.Status);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task FailedAttempt_PreservesLastSuccessAsStale()
    {
        var descriptor = Descriptor("flaky", CapabilityKind.RollingWindow, SourceKind.RollingWindowSnapshot);
        var method = new StubMethod(descriptor, call => call == 1
            ? Success(Window("five_hour", 42, DateTimeOffset.UtcNow.AddMinutes(-2)))
            : Failure());
        var runtime = NewCoordinator(method);
        try
        {
            var first = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            var key = MethodResultCache.MethodKey("page-1", first.Scan!.Fingerprint, descriptor.MethodId, descriptor.ImplementationVersion);
            var old = first.Snapshot with
            {
                Metadata = first.Snapshot.Metadata with { FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
            };
            runtime.Cache.Put(key, old);

            var second = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Poll, CancellationToken.None);

            Assert.Equal(SnapshotStatus.Stale, second.Snapshot.Status);
            Assert.True(second.Snapshot.Windows.Single().IsStale);
            Assert.Equal(first.Snapshot.Windows.Single().FetchedAt, second.Snapshot.Windows.Single().FetchedAt);
            Assert.NotNull(second.Failure);
            Assert.True(method.QueryCalls >= 2);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task AuthRequiredExtraCapability_DoesNotDowngradePageWithFreshUsage()
    {
        // 滚动窗口查询成功；OAuth 附加能力（如余额）等待登录不构成故障，整页保持 Success，
        // 登录入口仍通过 AuthCredentialClass 暴露给登录流程。
        var windowDescriptor = Descriptor("windows", CapabilityKind.RollingWindow, SourceKind.RollingWindowSnapshot);
        var windows = new StubMethod(windowDescriptor, _ => Success(Window("five_hour", 30)));

        var oauthDescriptor = new QueryMethodDescriptor(
            "oauth.extra", SourceKind.AllowanceOrBalance, CredentialClass.OAuthSession,
            QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
            SourceStability.OfficialConditional, MethodEnablement.Always, 30, "1.0.0");
        var oauth = new StubMethod(oauthDescriptor, _ => Failure(),
            MethodSupport.AuthRequired(oauthDescriptor, "需要全局 OAuth 会话"));

        var runtime = NewCoordinator(windows, oauth);
        try
        {
            var result = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);

            Assert.Equal(SnapshotStatus.Success, result.Snapshot.Status);
            Assert.Null(result.Failure);
            Assert.Empty(result.Snapshot.Balances);
            Assert.Single(result.Snapshot.Windows);
            Assert.Equal(CredentialClass.OAuthSession, result.AuthCredentialClass);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task AuthRequiredOnlyCapability_StillReportsAuthRequiredForLoginFlow()
    {
        // 页面没有任何可用能力时，等待登录必须保持“需要鉴权”，登录流程依赖该状态分发。
        var oauthDescriptor = new QueryMethodDescriptor(
            "oauth.only", SourceKind.AllowanceOrBalance, CredentialClass.OAuthSession,
            QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
            SourceStability.OfficialConditional, MethodEnablement.Always, 30, "1.0.0");
        var oauth = new StubMethod(oauthDescriptor, _ => Failure(),
            MethodSupport.AuthRequired(oauthDescriptor, "需要全局 OAuth 会话"));

        var runtime = NewCoordinator(oauth);
        try
        {
            var result = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);

            Assert.Equal(SnapshotStatus.AuthRequired, result.Snapshot.Status);
            Assert.NotNull(result.Failure);
            Assert.Equal(CredentialClass.OAuthSession, result.AuthCredentialClass);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task TemporaryOverride_IsInMemoryAndClearedByRescan()
    {
        var primaryDescriptor = Descriptor("primary", CapabilityKind.BalanceOrQuota, priority: 10);
        var alternateDescriptor = Descriptor("alternate", CapabilityKind.BalanceOrQuota, priority: 20);
        var primary = new StubMethod(primaryDescriptor, _ => Success(new BalanceQuotaValue(
            CapabilityKind.BalanceOrQuota, new SourceIdentity("primary", "a", "primary", "https://a.invalid"),
            new CredentialScope(CredentialClass.None), Coverage.Unknown, DateTimeOffset.UtcNow,
            1, false, false, 1m, null, null, null, "USD", null)));
        var alternate = new StubMethod(alternateDescriptor, _ => Success(new BalanceQuotaValue(
            CapabilityKind.BalanceOrQuota, new SourceIdentity("alternate", "a", "alternate", "https://a.invalid"),
            new CredentialScope(CredentialClass.None), Coverage.Unknown, DateTimeOffset.UtcNow,
            1, false, false, 2m, null, null, null, "USD", null)));
        var runtime = NewCoordinator(primary, alternate);
        try
        {
            await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            runtime.Coordinator.SetTemporaryOverride("page-1", alternateDescriptor.MethodId);
            var overridden = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Poll, CancellationToken.None);
            var serializedState = File.ReadAllText(Path.Combine(runtime.Directory, "runtime", "page-1.json"));

            Assert.Equal(alternateDescriptor.MethodId, overridden.Snapshot.Metadata.SelectedMethodId);
            Assert.DoesNotContain("TemporaryOverride", serializedState, StringComparison.OrdinalIgnoreCase);

            var scan = await runtime.Coordinator.RescanAsync(Page(), ScanReason.Manual, CancellationToken.None);
            Assert.Equal(primaryDescriptor.MethodId, scan.SelectedMethodId);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }
}
