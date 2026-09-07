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
        public CandidateStatus? ScanFailure { get; set; }
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
            return Task.FromResult(ScanFailure is { } status
                ? MethodSupport.NotAvailable(_descriptor, status, "scan failed") : _candidate);
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
            cache);
        return (coordinator, cache, stateStore, directory);
    }

    [Fact]
    public async Task RemovePage_ClearsSnapshotCacheAndPersistedScan()
    {
        var descriptor = Descriptor("remove", CapabilityKind.RollingWindow);
        var runtime = NewCoordinator(new StubMethod(descriptor, _ => Success(Window("window", 42))));
        try
        {
            var result = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            var key = MethodResultCache.MethodKey(Page().Id, result.Scan!.Fingerprint, descriptor.MethodId, descriptor.ImplementationVersion);
            Assert.True(runtime.Cache.TryGet(key, out _));
            await runtime.Coordinator.RemovePageAsync(Page().Id);
            Assert.False(runtime.Coordinator.TryGetSnapshot(Page().Id, out _));
            Assert.False(runtime.Coordinator.TryGetScanReport(Page().Id, out _));
            Assert.False(runtime.Cache.TryGet(key, out _));
            Assert.Null(runtime.StateStore.Load(Page().Id));
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task FailedRescan_PreservesHistoryButChangedIdentityDoesNot()
    {
        var method = new StubMethod(Descriptor("scan", CapabilityKind.RollingWindow),
            _ => Success(Window("window", 42)));
        var runtime = NewCoordinator(method);
        try
        {
            await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            method.ScanFailure = CandidateStatus.NetworkFailure;
            var failed = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            Assert.Equal(SnapshotStatus.Stale, failed.Snapshot.Status);
            Assert.True(failed.Snapshot.Windows.Single().IsStale);
            Assert.Equal(1, method.QueryCalls);
            var changed = Page();
            changed.BaseUrl = "https://different.invalid";
            var isolated = await runtime.Coordinator.RefreshAsync(changed, RefreshReason.Manual, CancellationToken.None);
            Assert.Empty(isolated.Snapshot.Windows);
            Assert.Equal(SnapshotStatus.TemporaryFailure, isolated.Snapshot.Status);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task RateLimit_ManualRefreshDoesNotBypassQueryCooldown()
    {
        var method = new StubMethod(Descriptor("limited", CapabilityKind.RollingWindow),
            _ => Failure(CandidateStatus.RateLimited));
        var runtime = NewCoordinator(method);
        try
        {
            await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            var second = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            Assert.Equal(1, method.QueryCalls);
            Assert.Equal(SnapshotStatus.RateLimited, second.Snapshot.Status);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public async Task TerminalFailure_PollWaitsForExplicitRescan()
    {
        var method = new StubMethod(Descriptor("auth", CapabilityKind.RollingWindow),
            _ => Failure(CandidateStatus.AuthRequired));
        var runtime = NewCoordinator(method);
        try
        {
            await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Poll, CancellationToken.None);
            Assert.Equal(1, method.QueryCalls);
            await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            Assert.Equal(2, method.QueryCalls);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    [Fact]
    public void FailurePriority_DoesNotDependOnMethodRegistrationOrder()
    {
        var failures = new[]
        {
            new FailureInfo(CandidateStatus.NetworkFailure, "network", DateTimeOffset.UtcNow),
            new FailureInfo(CandidateStatus.AuthRequired, "auth", DateTimeOffset.UtcNow),
        };
        Assert.Equal(CandidateStatus.AuthRequired, SnapshotPolicy.PrimaryFailure(failures)!.Status);
        Assert.Equal(SnapshotPolicy.PrimaryFailure(failures), SnapshotPolicy.PrimaryFailure(failures.Reverse()));
    }
    [Fact]
    public async Task ManualRescanFailure_PreservesRecentSuccess()
    {
        var descriptor = Descriptor("manual", CapabilityKind.RollingWindow);
        var method = new StubMethod(descriptor, call => call == 1
            ? Success(Window("window", 42)) : Failure(CandidateStatus.Forbidden));
        var runtime = NewCoordinator(method);
        try
        {
            var first = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            var second = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            Assert.Equal(2, method.QueryCalls);
            Assert.Equal(SnapshotStatus.Stale, second.Snapshot.Status);
            Assert.Equal(first.Snapshot.Windows.Single().FetchedAt, second.Snapshot.Windows.Single().FetchedAt);
            Assert.True(second.Snapshot.Windows.Single().IsStale);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
    }

    private sealed class ImmediateRetryTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task StaleFailures_RescanOnNextRoundWithoutRecursiveQuery()
    {
        var descriptor = Descriptor("stale", CapabilityKind.RollingWindow);
        var method = new StubMethod(descriptor, call => call == 1
            ? Success(Window("window", 42, DateTimeOffset.UtcNow.AddMinutes(-2))) : Failure());
        var runtime = NewCoordinator(method);
        var coordinator = new PageRuntimeCoordinator(new QueryMethodRegistry(new[] { method }),
            new FingerprintBuilder(new[] { descriptor }), runtime.StateStore, runtime.Cache,
            time: new ImmediateRetryTime());
        try
        {
            var first = await coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            var key = MethodResultCache.MethodKey(Page().Id, first.Scan!.Fingerprint, descriptor.MethodId, descriptor.ImplementationVersion);
            runtime.Cache.Put(key, first.Snapshot with
            {
                Metadata = first.Snapshot.Metadata with { FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
            });
            for (var i = 0; i < 3; i++)
            {
                var result = await coordinator.RefreshAsync(Page(), RefreshReason.Poll, CancellationToken.None);
                Assert.Equal(SnapshotStatus.Stale, result.Snapshot.Status);
                Assert.Null(result.Scan);
            }
            Assert.Equal(1, method.ScanCalls);
            Assert.Equal(10, method.QueryCalls);
            var recovered = await coordinator.RefreshAsync(Page(), RefreshReason.Poll, CancellationToken.None);
            Assert.NotNull(recovered.Scan);
            Assert.Equal(2, method.ScanCalls);
            Assert.Equal(13, method.QueryCalls);
        }
        finally { Directory.Delete(runtime.Directory, recursive: true); }
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
    public async Task LocalFallbackSource_IsNotSelectedWhenNoOnlineUsageAvailable()
    {
        // 线上化改造后本地记录来源不再注册/参与；ReportedUsage 无任何可用候选时应为空用量、不误报成功。
        var usageDescriptor = Descriptor("usage", CapabilityKind.ReportedUsage, SourceKind.RemoteOfficialStats);
        var usage = new StubMethod(usageDescriptor, _ => Failure(CandidateStatus.NoReliableUsage));
        var runtime = NewCoordinator(usage);
        try
        {
            var result = await runtime.Coordinator.RefreshAsync(Page(), RefreshReason.Manual, CancellationToken.None);
            Assert.Empty(result.Snapshot.ReportedUsages);
            Assert.Equal(SnapshotStatus.NoData, result.Snapshot.Status);
            Assert.Equal(1, usage.QueryCalls);
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
            await runtime.Coordinator.SetTemporaryOverrideAsync("page-1", alternateDescriptor.MethodId);
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
