using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Runtime;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class PageRefreshQueueTests
{
    [Fact]
    public async Task DuplicatePoll_MergesButManualRescanRemainsDistinct()
    {
        var catalog = new PageCatalog(new[] { new PageConfigRecord() });
        var page = catalog.Snapshot().Single();
        var queue = new PageRefreshQueue(catalog);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task Run(CancellationToken token) { Interlocked.Increment(ref calls); return release.Task.WaitAsync(token); }
        var first = queue.Run(page, RefreshReason.Poll, Run);
        var duplicate = queue.Run(page, RefreshReason.Poll, Run);
        var manual = queue.Run(page, RefreshReason.Manual, Run);
        Assert.Same(first, duplicate);
        Assert.NotSame(first, manual);
        release.SetResult();
        await Task.WhenAll(first, manual);
        Assert.Equal(2, calls);
        await queue.StopAsync();
    }

    [Fact]
    public async Task Save_CancelsOldRevisionAndStopWaitsForCleanup()
    {
        var catalog = new PageCatalog(new[] { new PageConfigRecord() });
        var page = catalog.Snapshot().Single();
        var queue = new PageRefreshQueue(catalog);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = queue.Run(page, RefreshReason.Poll, async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                cancelled.SetResult();
                await cleanup.Task;
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(catalog.Save(page, page.Revision, _ => new(true)).Succeeded);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = queue.StopAsync();
        Assert.False(stopping.IsCompleted);
        cleanup.SetResult();
        await stopping;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.Run(catalog.Snapshot().Single(),
            RefreshReason.Poll, _ => Task.CompletedTask));
    }
}
