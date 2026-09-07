using System.IO;
using System.Windows;
using System.Windows.Controls;
using TokenConsumptionMonitoring;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI;

internal static class EngineSmoke
{
    public static void Run(Application app, IPageCredentialStore credentials)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "engine-smoke-data");
        var store = new PageConfigStore(directory);
        var loaded = store.Load();
        var catalog = new PageCatalog(loaded.Document.Pages);
        var commands = new PageConfigurationCommands(catalog, store, credentials);
        if (catalog.Snapshot().Count == 0 && !File.Exists(Path.Combine(directory, "pages.json")))
            foreach (var name in new[] { "测试页面 A", "测试页面 B" })
                commands.Save(new PageConfigRecord
                {
                    Name = name, BaseUrl = "https://example.invalid",
                    Protocol = KeyFormat.Protocol.DeepSeekConsole.ToString(),
                    CredentialRef = CredentialReference.GlobalConsoleSession(AppIdentity.DeepSeekCookiesTarget),
                }, null, null);
        var method = new SimulatedMethod();
        var registry = new QueryMethodRegistry(new[] { method });
        var coordinator = new PageRuntimeCoordinator(registry, new FingerprintBuilder(registry.Descriptors),
            new MethodStateStore(directory), new MethodResultCache(), catalog: catalog);
        var state = new MonitorState();
        var settingsStore = new SettingsStore(directory);
        var settings = settingsStore.Load();
        var tray = new StatusSink();
        var engine = new PageEngine(catalog, state, new AlertService(settings, _ => { }), tray,
            settings, settingsStore, app.Dispatcher, coordinator);
        var panel = new MainPanel(commands, catalog, state, loaded.IsRecoveryRequired ? loaded.Diagnostic : null)
        {
            AllowClose = true, ShowInTaskbar = true, WindowStyle = WindowStyle.SingleBorderWindow,
            Title = "TCM Architecture Desktop Smoke — Engine",
        };
        var surface = (UIElement)panel.Content;
        panel.Content = null;
        var host = new DockPanel();
        var controls = new WrapPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(controls, Dock.Top);
        host.Children.Add(controls);
        host.Children.Add(surface);
        panel.Content = host;
        void Button(string label, Func<Task> action)
        {
            var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(6) };
            button.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception ex) { MessageBox.Show(ex.GetType().Name, "测试失败"); }
            };
            controls.Children.Add(button);
        }
        Button("模拟正常", async () => { method.Status = SnapshotStatus.Success; await engine.RefreshNowAsync(); });
        Button("模拟断网", async () => { method.Status = SnapshotStatus.TemporaryFailure; await engine.RefreshNowAsync(); });
        Button("模拟登录完成", async () =>
        { method.Status = SnapshotStatus.Success; await engine.RefreshAfterSessionAsync(CredentialClass.ConsoleSession); });
        Button("连续刷新两次", () => Task.WhenAll(engine.RefreshNowAsync(), engine.RefreshNowAsync()));
        controls.Children.Add(tray.Label);
        panel.PageSwitchRequested += id => engine.SetActivePage(id);
        panel.RefreshRequested += async () => await engine.RefreshNowAsync();
        panel.RescanRequested += async id => await engine.RescanById(id, ScanReason.Manual);
        panel.OverrideRequested += async (id, selected) => await engine.SetTemporaryOverrideAsync(id, selected);
        panel.PagesChanged += () =>
        {
            if (engine.ActivePage is null) engine.SetActivePage(engine.Pages.FirstOrDefault()?.Id);
            else state.SetPageState(true, engine.ActivePage.Name);
        };
        engine.ActivePageChanged += id => { if (!panel.IsEditingFormOpen) panel.SetActivePageId(id); };
        var stopping = false;
        var stopped = false;
        panel.Closing += async (_, e) =>
        {
            if (stopped) return;
            e.Cancel = true;
            if (stopping) return;
            stopping = true;
            panel.IsEnabled = false;
            await engine.StopAsync();
            engine.Dispose();
            stopped = true;
            File.WriteAllText(Path.Combine(directory, "last-stop.txt"), "Engine StopAsync completed");
            panel.Close();
        };
        panel.Loaded += (_, _) =>
        {
            engine.SetActivePage(catalog.Snapshot().FirstOrDefault(p => p.Id == settings.ActivePageId)?.Id
                ?? catalog.Snapshot().FirstOrDefault()?.Id);
            engine.Start();
        };
        app.Run(panel);
    }

    private sealed class StatusSink : ITrayStatusSink
    {
        public readonly TextBlock Label = new() { Margin = new Thickness(8), Foreground = System.Windows.Media.Brushes.White };
        public void SetState(ConnectionStatus status, AlertLevel level) => Label.Text = $"托盘输出：{status} / {level}";
    }

    private sealed class SimulatedMethod : IQueryMethod
    {
        public SnapshotStatus Status = SnapshotStatus.Success;
        public QueryMethodDescriptor Describe() => new("desktop.smoke.balance", SourceKind.AllowanceOrBalance,
            CredentialClass.None, QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
            SourceStability.OfficialStable, MethodEnablement.Always, 0, "1");
        public Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
            => Task.FromResult(MethodSupport.Available(Describe(), CredentialScope.None, Coverage.Unknown,
                Array.Empty<DetectionEvidence>()));
        public Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Status != SnapshotStatus.Success)
                return Task.FromResult(MethodQueryResult.Empty(Status, "桌面测试注入的网络故障"));
            var now = DateTimeOffset.UtcNow;
            var value = new BalanceQuotaValue(CapabilityKind.BalanceOrQuota,
                new SourceIdentity("smoke", page.Id, Describe().MethodId, "https://example.invalid"),
                CredentialScope.None, Coverage.Unknown, now, 1, false, false,
                page.Name.EndsWith("B") ? 200 : 100, null, null, null, "USD", null, now.AddMinutes(1));
            return Task.FromResult(new MethodQueryResult(new[] { value }, Status, null, now));
        }
    }
}
