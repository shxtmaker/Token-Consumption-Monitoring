using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>合并相同页面修订与原因的在途刷新，并跟踪取消和停止。</summary>
public sealed class PageRefreshQueue
{
    private readonly object _gate = new();
    private readonly PageCatalog _catalog;
    private readonly Dictionary<(string Id, long Revision, RefreshReason Reason), Work> _pending = new();
    private bool _stopped;

    private sealed class Work
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public PageRefreshQueue(PageCatalog catalog)
    {
        _catalog = catalog;
        catalog.Changed += OnChanged;
    }

    public Task Run(PageConfigRecord page, RefreshReason reason, Func<CancellationToken, Task> action)
    {
        var key = (page.Id, page.Revision, reason);
        lock (_gate)
        {
            if (_stopped || !_catalog.IsCurrent(page.Id, page.Revision))
                return Task.FromCanceled(new CancellationToken(true));
            if (_pending.TryGetValue(key, out var previous)) return previous.Completion.Task;
            var work = new Work();
            _pending.Add(key, work);
            _ = ExecuteAsync(key, work, action);
            return work.Completion.Task;
        }
    }

    private async Task ExecuteAsync((string Id, long Revision, RefreshReason Reason) key, Work work,
        Func<CancellationToken, Task> action)
    {
        await Task.Yield();
        Exception? failure = null;
        try
        {
            work.Cancellation.Token.ThrowIfCancellationRequested();
            await action(work.Cancellation.Token);
        }
        catch (Exception ex) { failure = ex; }
        lock (_gate)
        {
            _pending.Remove(key);
            if (failure is OperationCanceledException) work.Completion.TrySetCanceled();
            else if (failure is not null) work.Completion.TrySetException(failure);
            else work.Completion.TrySetResult();
            work.Cancellation.Dispose();
        }
    }

    private void OnChanged(string id)
    {
        lock (_gate)
        {
            foreach (var pair in _pending.Where(pair => pair.Key.Id == id
                         && !_catalog.IsCurrent(id, pair.Key.Revision)))
                _ = pair.Value.Cancellation.CancelAsync();
        }
    }

    public async Task StopAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            _stopped = true;
            _catalog.Changed -= OnChanged;
            tasks = _pending.Values.Select(work => work.Completion.Task).ToArray();
            foreach (var work in _pending.Values) _ = work.Cancellation.CancelAsync();
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        catch (Exception) { Logger.Log("刷新任务结束时存在失败，所有在途任务均已完成"); }
    }
}
