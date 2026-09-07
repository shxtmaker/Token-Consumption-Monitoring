using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.Services.Persistence;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class StalePublicationTests
{
    private sealed class DelayedMethod(string id = "delayed", int priority = 0) : IQueryMethod
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<MethodQueryResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public QueryMethodDescriptor Describe() => new(id, SourceKind.AllowanceOrBalance,
            CredentialClass.None, QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
            SourceStability.OfficialStable, MethodEnablement.Always, priority, "1");
        public Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
            => Task.FromResult(MethodSupport.Available(Describe(), CredentialScope.None, Coverage.Unknown,
                Array.Empty<DetectionEvidence>()));
        public Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
        {
            Started.TrySetResult();
            return Result.Task;
        }
    }

    [Fact]
    public async Task OverrideDuringRescan_AppliesAfterRescanCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_override_" + Guid.NewGuid().ToString("N"));
        var catalog = new PageCatalog(new[] { new PageConfigRecord() });
        var page = catalog.Snapshot().Single();
        var primary = new DelayedMethod();
        var alternate = new DelayedMethod("alternate", 10);
        alternate.Result.SetResult(MethodQueryResult.Empty(SnapshotStatus.NoData, "empty"));
        var registry = new QueryMethodRegistry(new[] { primary, alternate });
        var coordinator = new PageRuntimeCoordinator(registry, new FingerprintBuilder(registry.Descriptors),
            new MethodStateStore(directory), new MethodResultCache(), catalog: catalog);
        try
        {
            var scan = coordinator.RefreshAsync(page, RefreshReason.Manual, CancellationToken.None);
            await primary.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var change = coordinator.SetTemporaryOverrideAsync(page.Id, "alternate");
            Assert.False(change.IsCompleted);
            primary.Result.SetResult(MethodQueryResult.Empty(SnapshotStatus.NoData, "empty"));
            await scan;
            await change;
            var result = await coordinator.RefreshAsync(page, RefreshReason.Poll, CancellationToken.None);
            Assert.Equal("alternate", result.Snapshot.Metadata.SelectedMethodId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DefaultRegistry_ContainsNoLocalRecordMethodsOrDuplicateIds()
    {
        // 本测试仅检查默认装配目录，不调用需要真实客户端的扫描与查询。
        var registry = QueryMethodRegistry.BuildDefault(null!, null!, null!, null!, null!);
        Assert.DoesNotContain(registry.Descriptors, descriptor => descriptor.SourceKind == SourceKind.LocalRecord);
        Assert.Equal(registry.Descriptors.Count, registry.Descriptors.Select(descriptor => descriptor.MethodId).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinator_RejectsLatePublicationAndCompletesQueuedDeletion(bool delete)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tcm_late_" + Guid.NewGuid().ToString("N"));
        var catalog = new PageCatalog(new[] { new PageConfigRecord() });
        var page = catalog.Snapshot().Single();
        var method = new DelayedMethod();
        var registry = new QueryMethodRegistry(new[] { method });
        var store = new MethodStateStore(directory);
        var cache = new MethodResultCache();
        var coordinator = new PageRuntimeCoordinator(registry, new FingerprintBuilder(registry.Descriptors),
            store, cache, catalog: catalog);
        try
        {
            var pending = coordinator.RefreshAsync(page, RefreshReason.Manual, CancellationToken.None);
            await method.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = File.ReadAllBytes(Path.Combine(directory, "runtime", page.Id + ".json"));
            Task? removal = null;
            if (delete)
            {
                catalog.Delete(page.Id, page.Revision, _ => new(true));
                removal = coordinator.RemovePageAsync(page.Id);
                Assert.False(removal.IsCompleted);
            }
            else catalog.Save(page, page.Revision, _ => new(true));
            method.Result.SetResult(MethodQueryResult.Empty(SnapshotStatus.NoData, "empty"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(coordinator.TryGetSnapshot(page.Id, out _));
            if (removal is not null)
            {
                await removal;
                Assert.Null(store.Load(page.Id));
            }
            else Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "runtime", page.Id + ".json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateQuery_CannotRecordAttemptAfterSaveOrDelete(bool delete)
    {
        var catalog = new PageCatalog(new[] { new PageConfigRecord() });
        var page = catalog.Snapshot().Single();
        var cache = new MethodResultCache();
        var executor = new MethodExecutor(cache, catalog: catalog);
        var method = new DelayedMethod();
        var candidate = MethodSupport.NotAvailable(method.Describe(), CandidateStatus.NoReliableUsage, "test");
        var pending = executor.ExecuteAsync(page, method, candidate, "fingerprint", CancellationToken.None);
        await method.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (delete) catalog.Delete(page.Id, page.Revision, _ => new(true));
        else catalog.Save(page, page.Revision, _ => new(true));
        method.Result.SetResult(MethodQueryResult.Empty(SnapshotStatus.NoData, "empty"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(cache.TryGet(MethodResultCache.MethodKey(page.Id, "fingerprint", "delayed", "1"), out _));
    }
}
