using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TokenConsumptionMonitoring.Converters;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--live-codex")) return LiveCodex().GetAwaiter().GetResult();
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/official-query-verification");
        Directory.CreateDirectory(output);
        var app = new Application();
        app.Resources["ConnBrush"] = new ConnectionToBrushConverter();
        app.Resources["LevelBrush"] = new LevelToBrushConverter();
        app.Resources["Progress"] = new ProgressConverter();
        app.Resources["StrVis"] = new StringToVisibilityConverter();
        app.Resources["BoolVis"] = new BoolToVisibilityConverter();
        var catalog = new PageCatalog([]);
        var credentials = new MemoryCredentials();
        var commands = new PageConfigurationCommands(catalog, new MemoryPersistence(), credentials);
        var state = new MonitorState();
        var panel = new MainPanel(commands, catalog, state);
        var panelRoot = (FrameworkElement)panel.Content;
        panel.Content = null;
        ((Button)panel.FindName("AddProviderBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBox)panel.FindName("PNameBox")).Text = "OpenAI 组织用量";
        ((TextBox)panel.FindName("PBaseUrlBox")).Text = "https://api.openai.com";
        ((ComboBox)panel.FindName("PCredentialCombo")).SelectedValue = "AdminKey";
        Capture(panelRoot, state, 1080, 820, Path.Combine(output, "organization-form.png"));
        ((PasswordBox)panel.FindName("PKeyBox")).Password = "test-admin-secret";
        ((Button)panel.FindName("PSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (catalog.Snapshot().Single().CredentialRef.ResolveClass() != CredentialClass.AdminKey) throw new Exception("Admin choice was not persisted");
        ((Button)panel.FindName("AddProviderBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ComboBox)panel.FindName("PCredentialCombo")).SelectedValue = "LocalRecord";
        ((TextBox)panel.FindName("PBaseUrlBox")).Text = "https://chatgpt.com";
        ((TextBox)panel.FindName("PNameBox")).Text = "Codex 订阅";
        if (((PasswordBox)panel.FindName("PKeyBox")).IsEnabled) throw new Exception("Local session must not require a key");
        Capture(panelRoot, state, 720, 820, Path.Combine(output, "codex-form-narrow.png"));
        ((Button)panel.FindName("PSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!catalog.Snapshot().Any(p => p.CredentialRef.Kind == CredentialRefKind.LocalRecord)) throw new Exception("Local selection was not saved");
        ((Border)panel.FindName("ProviderForm")).Visibility = Visibility.Collapsed;
        var now = DateTimeOffset.UtcNow;
        var source = new SourceIdentity("fixture", "test", "test", "https://example.invalid");
        var scope = new CredentialScope(CredentialClass.ApiKey, "fixture");
        var codexPage = catalog.Snapshot().Single(p => p.CredentialRef.Kind == CredentialRefKind.LocalRecord);
        var windowMethod = Candidate(new CodexAccountMethod(false).Describe(), CandidateStatus.Available);
        var usageMethod = Candidate(new CodexAccountMethod(true).Describe(), CandidateStatus.AuthRequired);
        state.SetPageState(true, codexPage.Name);
        ApplyCandidates(windowMethod, usageMethod);
        Capture(panelRoot, state, 1080, 820, Path.Combine(output, "candidate-first-scan.png"));
        ApplyCandidates(windowMethod with { Status = CandidateStatus.NetworkFailure },
            usageMethod with { Status = CandidateStatus.Available });
        Capture(panelRoot, state, 1080, 820, Path.Combine(output, "candidate-rescan.png"));
        ApplyCandidates(windowMethod with { Status = CandidateStatus.NetworkFailure }, usageMethod);
        Capture(panelRoot, state, 1080, 820, Path.Combine(output, "candidate-empty.png"));

        void ApplyCandidates(params MethodCandidate[] candidates)
        {
            var plan = CapabilitySourcePlan.Build(candidates);
            var report = new ScanReport(codexPage.Id, "fixture", candidates, plan, now);
            state.Diagnostics.Update(codexPage, report, plan.PrimaryMethodId);
        }

        MethodCandidate Candidate(QueryMethodDescriptor method, CandidateStatus status) => new(
            method, status, 90, source, new CredentialScope(CredentialClass.LocalRecord, "Codex"),
            Coverage.Unknown, [], null);

        state.SetPageState(true, "Codex 订阅");
        state.ApplySnapshot(new CapabilitySnapshot
        {
            Metadata = new("test", "test", now, "test", RefreshReason.Poll), Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now, 1, false, false,
                    "primary", "Codex · 5 小时", "正常", null, null, null, 24, now.AddHours(3), "%"),
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now, 1, false, false,
                    "secondary", "Codex · 7 天", "正常", null, null, null, 61, now.AddDays(4), "%"),
                new ReportedUsageValue(CapabilityKind.ReportedUsage, source, scope,
                    new Coverage(null, null, Scope: "Codex 账户累计 Token · 非今日用量"), now, 1, false, false, 12345000, null, []),
            },
        }, true);
        var floating = new FloatingWindow();
        var floatingRoot = (FrameworkElement)floating.Content;
        floating.Content = null;
        Capture(floatingRoot, state, floating.Width, null, Path.Combine(output, "codex-widget.png"));
        state.ApplySnapshot(new CapabilitySnapshot
        {
            Metadata = new("test", "test", now, "test", RefreshReason.Poll), Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now, 1, false, false,
                    "short-primary", "5h 窗口", "正常", null, null, null, 24, now.AddHours(3), "%"),
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now, 1, false, false,
                    "short-secondary", "周窗口", "正常", null, null, null, 61, now.AddDays(4), "%"),
            },
        }, true);
        Capture(floatingRoot, state, floating.Width, null, Path.Combine(output, "compact-short-window-widget.png"));
        var shortWindowBars = VisualDescendants(floatingRoot).OfType<ProgressBar>().ToArray();
        if (shortWindowBars.Length != 2 || shortWindowBars.Any(bar => bar.ActualWidth < 100))
        {
            var message = $"Short window titles leave insufficient progress width: {string.Join(", ", shortWindowBars.Select(bar => bar.ActualWidth.ToString("F1")))} DIP";
            Console.Error.WriteLine(message);
            throw new Exception(message);
        }
        state.SetPageState(true, "DeepSeek 余额");
        state.ApplySnapshot(new CapabilitySnapshot
        {
            Metadata = new("test", "test", now, "test", RefreshReason.Poll), Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, now, 1, false, false, 12.34m, null, null, null, "CNY", null),
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, now, 1, false, false, 0.56m, null, null, null, "USD", null),
            },
        }, true);
        if (!state.Snapshot.BalanceLabel.Contains("CNY") || !state.Snapshot.BalanceLabel.Contains("USD")) throw new Exception("Missing currency");
        Capture(floatingRoot, state, floating.Width, null, Path.Combine(output, "multi-currency-widget.png"));
        state.SetPageState(true, "团队共享账户 · 长标题与模型用量汇总");
        state.ApplySnapshot(new CapabilitySnapshot
        {
            Metadata = new("test", "test", now, "test", RefreshReason.Poll), Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now, 1, false, false,
                    "short", "5 小时窗口", "正常", null, null, null, 8, now.AddHours(3).AddMinutes(47), "%"),
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now, 1, false, false,
                    "long", "团队共享长期额度窗口 · 全部模型与工具调用", "周加成生效：额外额度将在本周期结束时恢复。",
                    750_000_000, 10_000_000_000, 9_250_000_000, 7, now.AddDays(123).AddHours(5), "microcents"),
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now, 1, false, false,
                    "expired", "本周窗口", "正常", null, null, null, 100, now.AddMinutes(-1), "%"),
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now, 1, false, false,
                    "unknown", "额外额度窗口", null, null, null, null, null, null, "%"),
                new ReportedUsageValue(CapabilityKind.ReportedUsage, source, scope,
                    new Coverage(now.AddDays(-30), now, Scope: "组织全部项目与模型的报告用量"), now, 1, false, false,
                    12_987_654_321, 123_456,
                    new ModelUsageRow[]
                    {
                        new("team-production/reasoning-model-with-an-extra-long-deployment-name", 9_876_543_210, 1_234_567.89m, "CNY"),
                        new("gpt-5", 3_111_111_111, 987.65m, "CNY"),
                    }),
            },
        }, true);
        Capture(floatingRoot, state, floating.Width, null, Path.Combine(output, "alignment-long-content-widget.png"));

        state.SetPageState(true, "多币种余额与组织额度");
        state.ApplySnapshot(new CapabilitySnapshot
        {
            Metadata = new("test", "test", now, "test", RefreshReason.Poll), Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, now, 1, false, false,
                    1_234_567.8912m, null, null, null, "CNY", null),
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, now, 1, false, false,
                    123.45m, null, null, null, "USD", null),
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope,
                    new Coverage(null, null, Scope: "组织月度共享额度，包含全部项目、模型与工具调用，周期结束后重新计算。"), now, 1, false, false,
                    null, 1_234.5678m, 100_000.1234m, 98_765.5556m, "USD", null),
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, now, 1, false, false,
                    null, null, null, 0m, "EUR", null),
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, now, 1, false, false,
                    null, null, 500_000m, null, "CNY", null),
            },
        }, true);
        Capture(floatingRoot, state, floating.Width, null, Path.Combine(output, "alignment-multi-currency-quota-widget.png"));
        Console.WriteLine("PASS: credential controls, local-session key state, multi-currency projection, compact progress width and ten WPF renders");
        return 0;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static async Task<int> LiveCodex()
    {
        var page = new PageConfigRecord { BaseUrl = "https://chatgpt.com", CredentialRef = CredentialReference.LocalRecord };
        var ok = true;
        foreach (var usage in new[] { false, true })
        {
            var method = new CodexAccountMethod(usage);
            var result = await method.QueryAsync(page, null!, default);
            // 仅记录状态与条目数；不保存账户标识、实际额度或登录内容。
            Console.WriteLine($"{method.Describe().MethodId}: {result.Status}, capabilities={result.Capabilities.Count}");
            if (result.Status is not (SnapshotStatus.Success or SnapshotStatus.NoData)) ok = false;
        }
        return ok ? 0 : 1;
    }

    private static void Capture(FrameworkElement root, MonitorState state, double width, double? height, string path)
    {
        root.DataContext = state;
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        root.InvalidateMeasure();
        root.Measure(new Size(width, height ?? double.PositiveInfinity));
        var actualHeight = height ?? Math.Ceiling(root.DesiredSize.Height);
        root.Arrange(new Rect(0, 0, width, actualHeight));
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        root.InvalidateMeasure();
        root.Measure(new Size(width, height ?? double.PositiveInfinity));
        actualHeight = height ?? Math.Ceiling(root.DesiredSize.Height);
        root.Arrange(new Rect(0, 0, width, actualHeight));
        root.UpdateLayout();
        var image = new RenderTargetBitmap((int)width, (int)actualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        png.Save(stream);

    }

    private sealed class MemoryCredentials : IPageCredentialStore
    {
        private readonly Dictionary<string, string> _values = new();
        public bool TryRead(string target, out string? secret) => _values.TryGetValue(target, out secret);
        public void Write(string target, string secret) => _values[target] = secret;
        public void Delete(string target) => _values.Remove(target);
    }
    private sealed class MemoryPersistence : IPageConfigurationPersistence
    {
        public PageConfigurationSaveResult ValidateWrite(PageConfigDocument document) => new(true);
        public PageConfigurationSaveResult Save(PageConfigDocument document) => new(true);
    }
}
