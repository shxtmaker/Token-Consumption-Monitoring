using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI;

/// <summary>真实 OpenDesign 只读验收。数据与浏览器配置均保存到显式指定的独立目录。</summary>
internal static class OpenDesignSmoke
{
    public static void Run(Application app, string dashboard, string directory, bool verify, IPageCredentialStore credentials)
    {
        if (!OpenDesignConsoleMethod.TryGetWorkspace(dashboard, out _)) throw new ArgumentException("Expected OpenDesign workspace dashboard URL");
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var store = new PageConfigStore(directory);
        var catalog = new PageCatalog(store.Load().Document.Pages);
        var commands = new PageConfigurationCommands(catalog, store, credentials);
        if (catalog.Snapshot().Count == 0)
        {
            var editor = new PageEditorViewModel
            {
                Name = "OpenDesign", BaseUrl = dashboard, KeyClass = CredentialClass.ConsoleSession, EnableCompatibility = true,
            };
            var saved = editor.Save(commands, "");
            if (!saved.Succeeded) throw new InvalidOperationException(saved.Diagnostic);
        }
        var page = catalog.Snapshot().Single();
        if (page.BaseUrl != dashboard) throw new InvalidOperationException("The isolated data directory belongs to another workspace.");
        using var session = new OpenDesignConsoleSession(app.Dispatcher, Path.Combine(directory, "profiles"));
        using var deepSeek = new DeepSeekSessionService(app.Dispatcher);
        var observed = new ObservedReader(session, directory);
        var registry = QueryMethodRegistry.BuildDefault(new OpenCodeUsageClient(), new OpenCodeAuthService(new OAuthDeviceFlowClient()),
            deepSeek, new DeepSeekUsageClient(deepSeek), new CommandCodeUsageClient(), openDesign: observed);
        var coordinator = new PageRuntimeCoordinator(registry, new FingerprintBuilder(registry.Descriptors),
            new MethodStateStore(directory), new MethodResultCache(), catalog: catalog);
        var state = new MonitorState();
        var settingsStore = new SettingsStore(directory);
        var settings = settingsStore.Load();
        var engine = new PageEngine(catalog, state, new AlertService(settings, _ => { }), new QuietTray(), settings,
            settingsStore, app.Dispatcher, coordinator);
        var panel = new MainPanel(commands, catalog, state)
        {
            AllowClose = true, Title = verify ? "OpenDesign 重启验证" : "OpenDesign 首次登录验收", ShowInTaskbar = true,
        };
        var widget = new FloatingWindow { DataContext = state, Left = 60, Top = 60 };
        var recordingRestart = verify;
        var openingLogin = false;
        var surface = (UIElement)panel.Content;
        panel.Content = null;
        var host = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        var loginButton = new Button { Content = "登录 OpenDesign", Padding = new Thickness(12, 5, 12, 5) };
        var loginStatus = new TextBlock { Text = "首次使用请登录 OpenDesign。", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        controls.Children.Add(loginButton);
        controls.Children.Add(loginStatus);
        DockPanel.SetDock(controls, Dock.Top);
        host.Children.Add(controls);
        host.Children.Add(surface);
        panel.Content = host;
        var stopping = false;
        var stopped = false;
        panel.Closing += async (_, e) =>
        {
            if (stopped) return;
            e.Cancel = true;
            if (stopping) return;
            stopping = true;
            await engine.StopAsync();
            engine.Dispose();
            session.Dispose();
            widget.Close();
            stopped = true;
            panel.Close();
        };
        panel.RefreshRequested += async () => await engine.RefreshNowAsync();
        panel.RescanRequested += async id => await engine.RescanById(id, ScanReason.Manual);
        widget.RefreshRequested += async () => await engine.RefreshNowAsync();
        widget.OpenPanelRequested += () => { panel.Show(); panel.Activate(); };
        async Task OpenLogin()
        {
            if (openingLogin || stopping) return;
            openingLogin = true;
            loginButton.IsEnabled = false;
            // 交互登录后的读取只能证明首次读取，不能证明冷启动会话恢复。
            recordingRestart = false;
            panel.Title = "OpenDesign 首次登录验收";
            loginStatus.Text = "请在登录窗口中完成登录，再点击“完成并读取额度”。";
            try { await session.ShowLoginAsync(page, Validate); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "error.txt"), ex.GetType().Name + ": " + ex.Message);
                loginStatus.Text = "登录窗口打开失败，可再次点击登录。";
                MessageBox.Show(panel, "登录窗口打开失败：" + ex.Message, "OpenDesign", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { openingLogin = false; loginButton.IsEnabled = true; }
        }
        loginButton.Click += async (_, _) => await OpenLogin();
        panel.OpenDesignLoginRequested += async _ => await OpenLogin();
        async Task<string?> Validate()
        {
            var isRestart = recordingRestart;
            await engine.RefreshAfterSessionAsync(CredentialClass.ConsoleSession);
            if (!coordinator.TryGetSnapshot(page.Id, out var snapshot)) return "尚未取得快照";
            var report = new
            {
                status = snapshot.Status.ToString(), at = snapshot.Metadata.FetchedAt, restart = isRestart,
                methods = snapshot.Metadata.EffectiveSelectedMethodIds,
                balances = snapshot.Balances.Select(b => new { b.Balance, b.Currency, b.IsPrivate, b.IsStale }),
                windows = snapshot.Windows.Select(w => new { w.WindowName, w.Percent, w.ResetsAt, w.Unit, w.IsStale }),
                state.ConnectionLabel, state.Snapshot.BalanceLabel,
            };
            File.WriteAllText(Path.Combine(directory, isRestart ? "restart-result.json" : "live-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (snapshot.Status != SnapshotStatus.Success || !snapshot.Balances.Any()) return state.ConnectionLabel + "：" + state.LastError;
            loginStatus.Text = isRestart ? "会话恢复与额度读取成功。" : "登录与额度读取成功。";
            widget.Show();
            await app.Dispatcher.InvokeAsync(() =>
            {
                widget.UpdateLayout();
                Capture(widget, Path.Combine(directory, isRestart ? "restart-widget.png" : "live-widget.png"));
                Capture(panel, Path.Combine(directory, isRestart ? "restart-panel.png" : "live-panel.png"));
            }, DispatcherPriority.ContextIdle);
            File.WriteAllText(Path.Combine(directory, isRestart ? "restart-pass.txt" : "live-pass.txt"), "PASS: production queries, coordinator, engine and WPF projection; real provider session.");
            return null;
        }
        panel.Loaded += async (_, _) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "last-start.json"), JsonSerializer.Serialize(new
                {
                    mode = verify ? "restart-verification" : "interactive-login", at = DateTimeOffset.UtcNow,
                    processId = Environment.ProcessId,
                }));
                engine.SetActivePage(page.Id);
                if (verify)
                {
                    var error = await Validate();
                    if (error is not null)
                    {
                        File.WriteAllText(Path.Combine(directory, "restart-error.txt"), error);
                        if (coordinator.TryGetSnapshot(page.Id, out var snapshot) && snapshot.Status == SnapshotStatus.AuthRequired)
                            await OpenLogin();
                        else
                        {
                            loginStatus.Text = "额度读取失败，可点击登录后重试。";
                            MessageBox.Show(panel, $"重启验证未通过：{error}", "OpenDesign 重启验证", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    else panel.Close();
                }
                else await OpenLogin();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "error.txt"), ex.GetType().Name + ": " + ex.Message);
                MessageBox.Show(panel, "验收启动失败，详情已保存到独立验收目录中的 error.txt。", "OpenDesign",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        app.Run(panel);
    }

    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class QuietTray : ITrayStatusSink
    { public void SetState(ConnectionStatus status, AlertLevel level) { } }

    private sealed class ObservedReader(IOpenDesignConsoleReader inner, string directory) : IOpenDesignConsoleReader
    {
        public async Task<JsonElement> ReadAsync(PageConfigRecord page, bool windows, CancellationToken ct)
        {
            var result = await inner.ReadAsync(page, windows, ct);
            // 只记录结构键与状态；不保存原始响应、Cookie 或认证信息。
            var schema = new
            {
                status = result.TryGetProperty("status", out var status) ? status.GetString() : null,
                contextFields = result.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object
                    ? context.EnumerateObject().Select(p => p.Name).ToArray() : [],
                bodyFields = result.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object
                    ? body.EnumerateObject().Select(p => p.Name).ToArray() : [],
            };
            File.WriteAllText(Path.Combine(directory, windows ? "windows-schema.json" : "wallet-schema.json"), JsonSerializer.Serialize(schema));
            return result;
        }
    }
}
