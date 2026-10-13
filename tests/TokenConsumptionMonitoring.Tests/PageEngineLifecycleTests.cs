using System.Windows.Threading;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class PageEngineLifecycleTests
{
    [Theory]
    [InlineData("switch")]
    [InlineData("save")]
    [InlineData("delete")]
    public Task LateQuotaReferenceCannotPublishAfterPageChange(string change) => OnDispatcher(async () =>
    {
        var a = new PageConfigRecord { Name = "A", BaseUrl = "https://amr-link.open-design.ai/v1",
            QuotaReferenceModelId = "opendesign:model", QuotaReferencePlanId = "go" };
        var b = new PageConfigRecord { Name = "B" };
        var catalog = new PageCatalog([a, b]);
        a = catalog.Snapshot().Single(p => p.Id == a.Id);
        var coordinator = new ControlledCoordinator();
        var references = new DelayedReference();
        var state = new MonitorState();
        using var engine = Create(catalog, coordinator, state, new Tray(), references);
        engine.SetActivePage(a.Id, persist: false);
        await coordinator.Started(a.Id);
        coordinator.Pending[a.Id].SetResult(Result(a.Id, SnapshotStatus.NoData));
        if (change == "switch")
        {
            engine.SetActivePage(b.Id, persist: false);
            await coordinator.Started(b.Id);
            coordinator.Pending[b.Id].SetResult(Result(b.Id, SnapshotStatus.NoData));
        }
        else if (change == "delete") Assert.True(catalog.Delete(a.Id, a.Revision, _ => new(true)).Succeeded);
        else Assert.True(catalog.Save(a, a.Revision, _ => new(true)).Succeeded);
        references.Pending.SetResult(new("opendesign", DateTimeOffset.UtcNow, "fixture",
            [new("opendesign:model", "Model", "go", "Go", 120, false, "", "https://open-design.ai/pricing/")]));
        await Drain();
        Assert.Empty(state.QuotaReference.AmountLabel);
        if (change == "switch") Assert.False(state.QuotaReference.IsVisible);
        await engine.StopAsync();
    });

    private sealed class DelayedReference : TokenConsumptionMonitoring.Services.QuotaReferences.IOfficialQuotaCatalogService
    {
        public readonly TaskCompletionSource<ModelQuotaCatalog> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ModelQuotaCatalog> GetAsync(string provider, bool force, CancellationToken ct) => Pending.Task;
    }

    [Fact]
    public Task ApiPageRoutesPrivateConsoleAuthenticationToOpenDesign() => OnDispatcher(async () =>
    {
        var catalog = new PageCatalog([new PageConfigRecord
        {
            Name = "OpenDesign", BaseUrl = "https://amr-link.open-design.ai/v1", OpenDesignWorkspaceId = "workspace-a",
            CredentialRef = CredentialReference.PageApiKey("test-page"),
            EnabledCompatibilityMethods = ["opendesign.balance.console", "opendesign.windows.console"],
        }]);
        var page = catalog.Snapshot().Single();
        var coordinator = new ControlledCoordinator();
        using var engine = Create(catalog, coordinator, new MonitorState(), new Tray());
        var requested = new List<LoginKind>();
        engine.LoginRequired += kind => requested.Add(kind);
        engine.SetActivePage(page.Id, persist: false);
        await coordinator.Started(page.Id);
        coordinator.Pending[page.Id].SetResult(new(page.Id, Snapshot(page.Id, SnapshotStatus.AuthRequired), null, null, CredentialClass.ConsoleSession));
        await Drain();
        Assert.Equal(LoginKind.OpenDesignConsole, Assert.Single(requested));
        await engine.StopAsync();
    });

    [Fact]
    public Task PrivateConsoleLoginInvalidatesApiPageWithoutReplacingItsCredential() => OnDispatcher(async () =>
    {
        var catalog = new PageCatalog([new PageConfigRecord
        {
            Name = "OpenDesign", BaseUrl = "https://amr-link.open-design.ai/v1", OpenDesignWorkspaceId = "workspace-a",
            CredentialRef = CredentialReference.PageApiKey("test-page"),
            EnabledCompatibilityMethods = ["opendesign.balance.console", "opendesign.windows.console"],
        }]);
        var original = catalog.Snapshot().Single();
        var coordinator = new ControlledCoordinator();
        using var engine = Create(catalog, coordinator, new MonitorState(), new Tray());
        var refresh = engine.RefreshAfterSessionAsync(CredentialClass.ConsoleSession);
        await coordinator.Started(original.Id);
        coordinator.Pending[original.Id].SetResult(Result(original.Id, SnapshotStatus.NoData));
        await refresh;
        var current = catalog.Snapshot().Single();
        Assert.NotEqual(original.SessionGeneration, current.SessionGeneration);
        Assert.True(current.Revision > original.Revision);
        Assert.Equal(original.BaseUrl, current.BaseUrl);
        Assert.Equal(original.CredentialRef, current.CredentialRef);
        Assert.Equal(original.OpenDesignWorkspaceId, current.OpenDesignWorkspaceId);
        await engine.StopAsync();
    });

    [Fact]
    public Task SwitchingPage_ClearsTrayAndRejectsLateBackgroundResult() => OnDispatcher(async () =>
    {
        var a = new PageConfigRecord { Name = "A" };
        var b = new PageConfigRecord { Name = "B" };
        var catalog = new PageCatalog(new[] { a, b });
        var coordinator = new ControlledCoordinator();
        coordinator.Cache[a.Id] = Snapshot(a.Id, SnapshotStatus.AuthRequired);
        var state = new MonitorState();
        var tray = new Tray();
        using var engine = Create(catalog, coordinator, state, tray);
        engine.SetActivePage(a.Id, persist: false);
        await coordinator.Started(a.Id);
        Assert.Equal(ConnectionStatus.AuthError, tray.Status);
        engine.SetActivePage(b.Id, persist: false);
        await coordinator.Started(b.Id);
        Assert.Equal(ConnectionStatus.Unknown, tray.Status);
        var calls = tray.Calls;
        coordinator.Pending[a.Id].SetResult(Result(a.Id, SnapshotStatus.AuthRequired));
        await Drain();
        Assert.Equal("B", state.PageName);
        Assert.Equal(ConnectionStatus.Unknown, state.Connection);
        Assert.Equal(calls, tray.Calls);
        coordinator.Pending[b.Id].SetResult(Result(b.Id, SnapshotStatus.ProbeOnly));
        await Drain();
        Assert.Equal(ConnectionStatus.Ok, state.Connection);
        engine.SetActivePage(null, persist: false);
        Assert.Equal(ConnectionStatus.Unknown, tray.Status);
        Assert.Equal(AlertLevel.None, tray.Level);
        await engine.StopAsync();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OldRevision_CannotRenderAfterSaveOrDelete(bool delete) => OnDispatcher(async () =>
    {
        var catalog = new PageCatalog(new[] { new PageConfigRecord { Name = "A" } });
        var page = catalog.Snapshot().Single();
        var coordinator = new ControlledCoordinator();
        var state = new MonitorState();
        var tray = new Tray();
        using var engine = Create(catalog, coordinator, state, tray);
        engine.SetActivePage(page.Id, persist: false);
        await coordinator.Started(page.Id);
        var calls = tray.Calls;
        if (delete) Assert.True(catalog.Delete(page.Id, page.Revision, _ => new(true)).Succeeded);
        else Assert.True(catalog.Save(page, page.Revision, _ => new(true)).Succeeded);
        coordinator.Pending[page.Id].SetResult(Result(page.Id, SnapshotStatus.AuthRequired));
        await Drain();
        Assert.Equal(ConnectionStatus.Unknown, state.Connection);
        Assert.Equal(calls, tray.Calls);
        await engine.StopAsync();
        Assert.Equal(delete ? 1 : 0, coordinator.Removals);
    });

    [Fact]
    public Task Stop_WaitsForUncooperativeQueryAndSuppressesItsUiCallback() => OnDispatcher(async () =>
    {
        var catalog = new PageCatalog(new[] { new PageConfigRecord() });
        var page = catalog.Snapshot().Single();
        var coordinator = new ControlledCoordinator();
        var state = new MonitorState();
        var tray = new Tray();
        using var engine = Create(catalog, coordinator, state, tray);
        engine.SetActivePage(page.Id, persist: false);
        await coordinator.Started(page.Id);
        var calls = tray.Calls;
        var stop = engine.StopAsync();
        Assert.False(stop.IsCompleted);
        coordinator.Pending[page.Id].SetResult(Result(page.Id, SnapshotStatus.AuthRequired));
        await stop;
        await Drain();
        Assert.Equal(ConnectionStatus.Unknown, state.Connection);
        Assert.Equal(calls, tray.Calls);
    });

    private static PageEngine Create(PageCatalog catalog, ControlledCoordinator coordinator,
        MonitorState state, Tray tray, TokenConsumptionMonitoring.Services.QuotaReferences.IOfficialQuotaCatalogService? references = null)
    {
        var settings = new AppSettings();
        return new(catalog, state, new AlertService(settings, _ => throw new Exception("Unexpected alert")),
            tray, settings, new SettingsStore(Path.Combine(Path.GetTempPath(), "unused_tcm_engine_test")),
            Dispatcher.CurrentDispatcher, coordinator, references);
    }

    private static CapabilitySnapshot Snapshot(string id, SnapshotStatus status)
        => CapabilitySnapshot.Empty(id, "test") with { Status = status };
    private static PageRuntimeResult Result(string id, SnapshotStatus status)
        => new(id, Snapshot(id, status), null, null);
    private static async Task Drain()
    {
        // ContextIdle runs after query continuations and the queued runtime projection.
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception ex) { finished.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class Tray : ITrayStatusSink
    {
        public ConnectionStatus Status;
        public AlertLevel Level;
        public int Calls;
        public void SetState(ConnectionStatus status, AlertLevel level)
        { Status = status; Level = level; Calls++; }
    }

    private sealed class ControlledCoordinator : IPageRuntimeCoordinator
    {
        public readonly Dictionary<string, CapabilitySnapshot> Cache = new();
        public readonly Dictionary<string, TaskCompletionSource<PageRuntimeResult>> Pending = new();
        public int Removals;
        private readonly Dictionary<string, TaskCompletionSource> _started = new();
        private TaskCompletionSource StartSignal(string id)
        {
            if (!_started.TryGetValue(id, out var signal))
                _started[id] = signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return signal;
        }
        public Task Started(string id) => StartSignal(id).Task;
        public Task<PageRuntimeResult> RefreshAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
        {
            var source = new TaskCompletionSource<PageRuntimeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(page.Id, source);
            StartSignal(page.Id).TrySetResult();
            return source.Task; // Deliberately ignores cancellation to exercise the publication guard.
        }
        public bool TryGetSnapshot(string id, out CapabilitySnapshot snapshot) => Cache.TryGetValue(id, out snapshot!);
        public Task<ScanReport> RescanAsync(PageConfigRecord page, ScanReason reason, CancellationToken ct)
            => throw new NotSupportedException();
        public Task SetTemporaryOverrideAsync(string id, string? method) => Task.CompletedTask;
        public Task RemovePageAsync(string id) { Removals++; return Task.CompletedTask; }
    }
}
