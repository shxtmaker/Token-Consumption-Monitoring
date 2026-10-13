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
using TokenConsumptionMonitoring.Services.QuotaReferences;
using TokenConsumptionMonitoring.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--live-quota-reference")) return LiveQuotaReference().GetAwaiter().GetResult();
        if (args.Contains("--verify-quota-account")) return VerifyQuotaAccount().GetAwaiter().GetResult();
        if (args.Contains("--verify-opendesign-pool")) return VerifyOpenDesignPool(args.Contains("--isolated-live-profile"));
        if (args.Contains("--live-codex")) return LiveCodex().GetAwaiter().GetResult();
        if (args.Contains("--live-providers")) return LiveProviders();
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
        var references = new FixtureReferences();
        var panel = new MainPanel(commands, catalog, state, quotaReferences: references);
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
        ((Button)panel.FindName("AddProviderBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBox)panel.FindName("PNameBox")).Text = "OpenDesign";
        const string openDesignEndpoint = "https://amr-link.open-design.ai/v1";
        ((TextBox)panel.FindName("PBaseUrlBox")).Text = openDesignEndpoint;
        ((PasswordBox)panel.FindName("PKeyBox")).Password = "test-opendesign-key";
        ((CheckBox)panel.FindName("PCompatibilityBox")).IsChecked = true;
        ((TextBox)panel.FindName("POpenDesignWorkspaceBox")).Text = OpenDesignConsoleMethod.DashboardUrl("workspace-a");
        ((ComboBox)panel.FindName("PQuotaModelCombo")).SelectedValue = "opendesign:deepseek v4.1 flash";
        ((ComboBox)panel.FindName("PQuotaPlanCombo")).SelectedValue = "go";
        if ((string)((ComboBox)panel.FindName("PCredentialCombo")).SelectedValue != "ApiKey"
            || !((PasswordBox)panel.FindName("PKeyBox")).IsEnabled
            || ((Border)panel.FindName("POpenDesignCompatibilityPanel")).Visibility != Visibility.Visible)
            throw new Exception("OpenDesign private compatibility replaced the API credential controls");
        Capture(panelRoot, state, 1080, 1000, Path.Combine(output, "opendesign-private-form.png"));
        Capture(panelRoot, state, 620, 1000, Path.Combine(output, "opendesign-private-form-narrow.png"));
        ((Button)panel.FindName("PSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var openDesignPage = catalog.Snapshot().Single(p => p.Name == "OpenDesign");
        if (openDesignPage.BaseUrl != openDesignEndpoint || openDesignPage.OpenDesignWorkspaceId != "workspace-a"
            || openDesignPage.CredentialRef.ResolveClass() != CredentialClass.ApiKey
            || commands.ReadApiKeyForEditing(openDesignPage) != "test-opendesign-key")
            throw new Exception("OpenDesign private settings did not preserve the API configuration");
        ((Button)panel.FindName("EditPageBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (((TextBox)panel.FindName("POpenDesignWorkspaceBox")).Text != "workspace-a")
            throw new Exception("OpenDesign workspace did not reload in the editor");
        if ((string?)((ComboBox)panel.FindName("PQuotaModelCombo")).SelectedValue != "opendesign:deepseek v4.1 flash"
            || (string?)((ComboBox)panel.FindName("PQuotaPlanCombo")).SelectedValue != "go"
            || openDesignPage.QuotaReferenceModelId != "opendesign:deepseek v4.1 flash")
            throw new Exception("Quota reference selection did not survive save and reload");
        string? openDesignLoginPage = null;
        panel.OpenDesignLoginRequested += id => openDesignLoginPage = id;
        ((Button)panel.FindName("POpenDesignLoginButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (openDesignLoginPage != openDesignPage.Id) throw new Exception("OpenDesign private login was not dispatched");
        ((Button)panel.FindName("AddProviderBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBox)panel.FindName("PNameBox")).Text = "Command Code";
        ((TextBox)panel.FindName("PBaseUrlBox")).Text = "https://api.commandcode.ai/provider/v1";
        ((PasswordBox)panel.FindName("PKeyBox")).Password = "test-commandcode-key";
        ((ComboBox)panel.FindName("PQuotaModelCombo")).SelectedValue = "commandcode:deepseek v4.1 flash";
        ((ComboBox)panel.FindName("PQuotaPlanCombo")).SelectedValue = "go";
        Capture(panelRoot, state, 620, 1000, Path.Combine(output, "commandcode-reference-form-narrow.png"));
        ((Button)panel.FindName("PSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)panel.FindName("EditPageBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var commandPage = catalog.Snapshot().Single(p => p.Name == "Command Code");
        if ((string?)((ComboBox)panel.FindName("PQuotaModelCombo")).SelectedValue != "commandcode:deepseek v4.1 flash"
            || commandPage.QuotaReferencePlanId != "go" || commands.ReadApiKeyForEditing(commandPage) != "test-commandcode-key")
            throw new Exception("Command Code quota selection did not preserve API credentials or survive reload");
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
        state.QuotaReference.Update(openDesignPage, references.Catalog("opendesign"));
        state.SetPageState(true, "OpenDesign");
        var odSource = new SourceIdentity("opendesign", "fixture", OpenDesignConsoleMethod.WindowsMethodId, "https://amr-api.open-design.ai", openDesignPage.OpenDesignWorkspaceId);
        var odScope = new CredentialScope(CredentialClass.ConsoleSession, "opendesign", Team: openDesignPage.OpenDesignWorkspaceId);
        state.ApplySnapshot(new CapabilitySnapshot
        {
            Metadata = new(openDesignPage.Id, "test", now, "test", RefreshReason.Poll), Status = SnapshotStatus.Success,
            Capabilities = [
                new RollingWindowValue(CapabilityKind.RollingWindow, odSource, odScope, Coverage.Unknown, now, 1, true, false,
                    "five-hour", "5 小时", "正常", null, null, null, 24, now.AddHours(3), "%")
                    { SharedQuota = new("go", "18000", 38000, 50000, 10000) },
                new RollingWindowValue(CapabilityKind.RollingWindow, odSource, odScope, Coverage.Unknown, now, 1, true, false,
                    "weekly", "7 天", "正常", null, null, null, 61, now.AddDays(4), "%")
                    { SharedQuota = new("go", "604800", 58500, 150000, 10000) },
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source with { MethodId = OpenDesignConsoleMethod.BalanceMethodId }, scope,
                    Coverage.Unknown, now, 1, true, false, 999, null, null, null, "USD", null)],
        }, true);
        if (state.Snapshot.HasBalance) throw new Exception("Legacy OpenDesign wallet is still visible");
        Capture(floatingRoot, state, floating.Width, null, Path.Combine(output, "opendesign-reference-widget.png"));
        Capture(panelRoot, state, 720, 1000, Path.Combine(output, "opendesign-reference-panel.png"));
        var referenceLabels = VisualDescendants(floatingRoot).OfType<TextBlock>().Where(t => t.ActualHeight > 0 && t.Visibility == Visibility.Visible).Select(t => t.Text).ToArray();
        if (!referenceLabels.Any(t => t.Contains("$120")) || !referenceLabels.Any(t => t.Contains("估算"))
            || referenceLabels.Any(t => t.Contains("剩余（估算）") || t.Contains("当前套餐可用"))) throw new Exception("Quota reference is not visible or not labeled as an estimate");
        state.QuotaReference.Update(commandPage, references.Catalog("commandcode"));
        var ccSource = new SourceIdentity("commandcode", "fixture", "commandcode.allowance-window.compat", "https://api.commandcode.ai/alpha");
        SharedQuotaEvidence CcFact(string period, decimal remaining, decimal? limit) => new("individual-go-v1", period, remaining, limit,
            SubscriptionStatus: "active", Quantity: 1, PeriodStart: now.AddDays(-1), PeriodEnd: now.AddDays(20), WindowsLimited: true);
        state.ApplySnapshot(new CapabilitySnapshot { Metadata = new(commandPage.Id, "fixture", now, null, RefreshReason.Poll), Status = SnapshotStatus.Success,
            Capabilities = [
                new RollingWindowValue(CapabilityKind.RollingWindow, ccSource, scope, Coverage.Unknown, now, 1, true, false, "five", "5 小时", null, null, null, null, 0, null, "%") { SharedQuota = CcFact("commandcode.fiveHour", 2, 2) },
                new RollingWindowValue(CapabilityKind.RollingWindow, ccSource, scope, Coverage.Unknown, now, 1, true, false, "week", "7 天", null, null, null, null, 56, now.AddDays(5), "%") { SharedQuota = CcFact("commandcode.weekly", 2.2m, 5) },
                new RollingWindowValue(CapabilityKind.RollingWindow, ccSource, scope, Coverage.Unknown, now, 1, true, false, "commandcode.monthly", "月度限额", null, null, null, null, 41, now.AddDays(6), "%") { SharedQuota = CcFact("monthly", 5.9m, null), PercentIsDirectlyReported = false }
            ] }, true);
        state.SetPageState(true, "Command Code");
        Capture(floatingRoot, state, floating.Width, null, Path.Combine(output, "commandcode-monthly-percent-widget.png"));
        if (state.QuotaReference.RemainingLabel.Length != 0 || state.Snapshot.HasBalance
            || !state.Snapshot.Windows.Any(w => w.Name == "月度限额" && w.Percent == 41))
            throw new Exception("Command Code monthly percentage missing or dollar remaining still visible");
        state.QuotaReference.Configure(null);
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
        state.SetPageState(true, "Fireworks AI");
        state.ApplySnapshot(new CapabilitySnapshot
        {
            Metadata = new("test", "test", now, "fireworks.monthly-cost.api-key", RefreshReason.Poll),
            Status = SnapshotStatus.Success,
            Capabilities = [new ReportedCostValue(CapabilityKind.ReportedCost, source, scope,
                new Coverage(null, null, Scope: "账户本月消费"), now, 1, false, false, 0.77m, "USD")],
        }, true);
        Capture(floatingRoot, state, floating.Width, null, Path.Combine(output, "reported-cost-widget.png"));
        AssertVisibleCosts(floatingRoot, state);
        Console.WriteLine("PASS: quota model/plan save and reload, API credentials preserved, legacy OpenDesign wallet hidden, estimate labels, narrow forms, compact progress width and WPF renders");
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

    private static void AssertVisibleCosts(FrameworkElement root, MonitorState state)
    {
        var labels = VisualDescendants(root).OfType<TextBlock>().Where(t => t.ActualHeight > 0 && t.Visibility == Visibility.Visible)
            .Select(t => t.Text).ToArray();
        foreach (var row in state.Snapshot.ReportedCostRows)
            if (!labels.Contains(row.Label)) throw new Exception("Reported cost is missing from the rendered desktop widget");
    }

    private static int LiveProviders()
    {
        var output = Path.GetFullPath("artifacts/live-provider-verification");
        Directory.CreateDirectory(output);
        var app = new Application();
        app.Resources["ConnBrush"] = new ConnectionToBrushConverter();
        app.Resources["LevelBrush"] = new LevelToBrushConverter();
        app.Resources["Progress"] = new ProgressConverter();
        app.Resources["StrVis"] = new StringToVisibilityConverter();
        app.Resources["BoolVis"] = new BoolToVisibilityConverter();
        var pages = new PageConfigStore().Load().Document.Pages;
        var session = new DeepSeekSessionService(app.Dispatcher);
        using var fireworks = new FireworksConsoleSession(app.Dispatcher);
        var registry = QueryMethodRegistry.BuildDefault(new OpenCodeUsageClient(), new OpenCodeAuthService(new OAuthDeviceFlowClient()),
            session, new DeepSeekUsageClient(session), new CommandCodeUsageClient(), fireworks);
        var coordinator = new TokenConsumptionMonitoring.Services.Runtime.PageRuntimeCoordinator(registry,
            new FingerprintBuilder(registry.Descriptors), new TokenConsumptionMonitoring.Services.Persistence.MethodStateStore(output),
            new TokenConsumptionMonitoring.Services.Runtime.MethodResultCache());
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
          var ok = true;
          try
          {
           foreach (var page in pages)
           {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await coordinator.RefreshAsync(page, RefreshReason.Manual, timeout.Token);
            var state = new MonitorState();
            state.SetPageState(true, page.Name);
            state.ApplySnapshot(result.Snapshot, true);
            var window = new FloatingWindow();
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            Capture(root, state, 290, null, Path.Combine(output, page.Id + ".png"));
            AssertVisibleCosts(root, state);
            var pass = state.Snapshot.HasCapabilities && result.Snapshot.Status == SnapshotStatus.Success
                && (!page.EnabledCompatibilityMethods.Contains(FireworksConsoleBalanceMethod.MethodId) || state.Snapshot.HasBalance);
            Console.WriteLine($"{page.Name}: {(pass ? "PASS" : "FAIL")} status={result.Snapshot.Status}, windows={state.Snapshot.Windows.Count}, balances={state.Snapshot.BalanceRows.Count}, costs={state.Snapshot.ReportedCostRows.Count}");
            ok &= pass;
           }
          }
          catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); ok = false; }
          finally { fireworks.Dispose(); app.Shutdown(ok ? 0 : 1); }
        };
        return app.Run();
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

    private static async Task<int> LiveQuotaReference()
    {
        var output = Path.GetFullPath("artifacts/model-quota-verification");
        Directory.CreateDirectory(output);
        using var service = new OfficialQuotaCatalogService(Path.Combine(output, "public-cache"));
        foreach (var provider in new[] { "opendesign", "commandcode" })
        {
            var result = await service.GetAsync(provider, true, default);
            if (result.IsStale || result.Entries.Count == 0) throw new Exception("Live quota reference was not refreshed");
            Console.WriteLine($"{provider}: models={result.Entries.Select(e => e.ModelId).Distinct().Count()}, entries={result.Entries.Count}, version={result.Version}");
            File.WriteAllText(Path.Combine(output, provider + "-live.json"), System.Text.Json.JsonSerializer.Serialize(result));
            foreach (var example in result.Entries.Where(e => e.PlanId == "go" && (e.ModelName.Contains("Kimi K2.7") || e.ModelName == "DeepSeek V4.1 Flash" || e.ModelName == "GPT-6 Luna")))
                Console.WriteLine($"  {example.ModelName} / {example.PlanName}: {example.MonthlyUsd} USD ({example.Note})");
        }
        return 0;
    }

    private static int VerifyOpenDesignPool(bool isolated)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var directory = isolated ? Path.GetFullPath("artifacts/opendesign-live") : null;
            using var session = new OpenDesignConsoleSession(app.Dispatcher, directory is null ? null : Path.Combine(directory, "profiles"));
            var code = 0;
            try
            {
                var page = new PageConfigStore(directory).Load().Document.Pages.FirstOrDefault(p => OpenDesignConsoleMethod.TryGetWorkspace(p, out _));
                if (page is null) { Console.WriteLine("OpenDesign account: NOT_RUN (no configured workspace)"); return; }
                page.QuotaReferenceModelId = "opendesign:deepseek v4.1 flash";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                var observation = await session.ReadAsync(page, true, timeout.Token);
                var status = observation.GetProperty("status").GetString();
                Console.WriteLine("OpenDesign account status=" + status);
                if (status != "ready") { code = 1; return; }
                var body = observation.GetProperty("body");
                Console.WriteLine("OpenDesign tier=" + body.GetProperty("tier").GetString());
                if (observation.TryGetProperty("creditsPerUsd", out var scale)) Console.WriteLine("OpenDesign creditsPerUsd=" + scale);
                foreach (var window in body.GetProperty("windows").EnumerateArray())
                    Console.WriteLine($"window seconds={window.GetProperty("durationSeconds")}; limitCredits={window.GetProperty("limitCredits")}; remainingCredits={window.GetProperty("remainingCredits")}");
                page.QuotaReferencePlanId = body.GetProperty("tier").GetString();
                var result = await new OpenDesignConsoleMethod(new ObservedOpenDesign(observation), true).QueryAsync(page, null!, timeout.Token);
                using var catalogs = new OfficialQuotaCatalogService(Path.GetFullPath("artifacts/model-quota-verification/public-cache"));
                var catalog = await catalogs.GetAsync("opendesign", false, timeout.Token);
                VerifyRemainingModels(page, catalog, result, true);
            }
            catch (Exception ex) { Console.WriteLine("OpenDesign account: BLOCKED (" + ex.GetType().Name + ", " + ex.HResult.ToString("X8") + "): " + ex.Message); code = 1; }
            finally { session.Dispose(); app.Shutdown(code); }
        };
        return app.Run();
    }

    private static async Task<int> VerifyQuotaAccount()
    {
        // 只读账务请求。仅输出字段存在性，不输出凭据、账户标识或账务数值。
        var pages = new PageConfigStore().Load().Document.Pages;
        var page = pages.FirstOrDefault(p => OfficialQuotaCatalogService.ProviderOf(p.BaseUrl) == "commandcode");
        string? key = null;
        if (page?.CredentialRef.Target is { Length: > 0 } target) CredentialStore.TryReadSecret(target, out key);
        if (page is not null && string.IsNullOrEmpty(key)) key = CommandCodeUsageClient.ReadLocalApiKey();
        if (string.IsNullOrEmpty(key)) { Console.WriteLine("CommandCode actual account: NOT_RUN (no configured credential)"); return 0; }
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        try
        {
            var usage = await new CommandCodeUsageClient().FetchUsageAsync(key, ct.Token);
            Console.WriteLine($"CommandCode account read: PASS; monthlyRemaining={usage?.Credits?.MonthlyRemaining is not null}; fiveHour={usage?.Credits?.Limits?.FiveHour is not null}; weekly={usage?.Credits?.Limits?.Weekly is not null}; billingPeriod={usage?.Subscription?.CurrentPeriodStart is not null && usage?.Subscription?.CurrentPeriodEnd is not null}");
            Console.WriteLine($"CommandCode monthly meter: percent={usage?.Monthly?.UsedPercent}; directlyReported={usage?.Monthly?.IsDirectlyReported}; reset={usage?.Monthly?.ResetsAt:yyyy-MM-dd}");
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false });
            foreach (var endpoint in new[] { "credits", "subscriptions" })
            {
                var url = CommandCodeUsageClient.ApiBase + "/alpha/billing/" + endpoint
                    + (usage?.OrgId is { } org ? "?orgId=" + Uri.EscapeDataString(org) : "");
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                using var response = await http.SendAsync(request, ct.Token);
                response.EnsureSuccessStatusCode();
                using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct.Token));
                var root = document.RootElement;
                Console.WriteLine(endpoint + " root fields: " + Fields(root));
                if (root.TryGetProperty("windowLimits", out var wl)) foreach (var wn in new[] {"fiveHour", "weekly"}) if (wl.TryGetProperty(wn, out var wj)) Console.WriteLine(wn + " fields: " + Fields(wj));
                foreach (var property in new[] { "credits", "windowLimits", "data" })
                    if (root.TryGetProperty(property, out var child)) Console.WriteLine(endpoint + "." + property + " fields: " + Fields(child));
                if (endpoint == "subscriptions" && root.TryGetProperty("data", out var subscription))
                    foreach (var field in new[] { "planId", "status", "quantity" })
                        if (subscription.TryGetProperty(field, out var value)) Console.WriteLine("subscription." + field + "=" + value.ToString());
            }
            if (usage is null) throw new Exception("No actual account data");
            page!.QuotaReferencePlanId = usage.Subscription?.PlanId switch { "individual-goat" => "goat", "individual-pro-v1" => "pro", "individual-go-v1" => "go", _ => null };
            var method = new CommandCodeAllowanceWindowMethod(new ObservedCommandCode(usage));
            var candidate = MethodSupport.Available(method.Describe(), new(CredentialClass.ApiKey, "commandcode"), Coverage.Unknown, []);
            var actual = await method.QueryAsync(page, candidate, ct.Token);
            foreach (var w in actual.Capabilities.OfType<RollingWindowValue>()) Console.WriteLine($"window={w.WindowKey}; hasReset={w.ResetsAt is not null}; resetElapsed={w.ResetsAt <= DateTimeOffset.UtcNow}; full={w.SharedQuota?.Remaining == w.SharedQuota?.Limit}; usedZero={w.Used == 0}; ageSeconds={(DateTimeOffset.UtcNow-w.FetchedAt).TotalSeconds:0}; reset={w.ResetsAt:O}; remainingPresent={w.SharedQuota?.Remaining is not null}; limitPresent={w.SharedQuota?.Limit is not null}");
            using var catalogs = new OfficialQuotaCatalogService(Path.GetFullPath("artifacts/model-quota-verification/public-cache"));
            var catalog = await catalogs.GetAsync("commandcode", false, ct.Token);
            VerifyRemainingModels(page, catalog, actual, false);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"CommandCode account read: BLOCKED ({ex.GetType().Name})"); return 1; }
        static string Fields(System.Text.Json.JsonElement value) => value.ValueKind == System.Text.Json.JsonValueKind.Object
            ? string.Join(",", value.EnumerateObject().Select(p => p.Name).OrderBy(n => n)) : value.ValueKind.ToString();
    }

    private static void VerifyRemainingModels(PageConfigRecord page, ModelQuotaCatalog catalog, MethodQueryResult actual, bool showAmounts)
    {
        var snapshot = new CapabilitySnapshot { Metadata = new(page.Id, "live-read", DateTimeOffset.UtcNow, null, RefreshReason.Manual),
            Status = actual.Status, Capabilities = actual.Capabilities };
        foreach (var name in new[] { "deepseek v4.1 flash", "gpt-6 luna" })
        {
            page.QuotaReferenceModelId = catalog.Provider + ":" + name;
            var result = ModelRemainingEstimator.Estimate(page, catalog, snapshot, DateTimeOffset.UtcNow);
            if (result.Amounts.Count == 0 && result.Reason is null) { Console.WriteLine(catalog.Provider + " / " + name + ": percentage display PASS; no dollar remaining estimate"); continue; }
            if (result.Amounts.Count == 0) { Console.WriteLine("Model conversion unavailable: " + result.Reason); throw new Exception("Conversion unavailable"); }
            Console.WriteLine(catalog.Provider + " / " + name + ": actual account conversion PASS; periods=" + string.Join(",", result.Amounts.Keys));
            if (showAmounts) Console.WriteLine(result.Label);
        }
        Console.WriteLine("Read-only account plus public pricing verification PASS; billed inference/debit experiment NOT_RUN");
    }

    private sealed class ObservedOpenDesign(System.Text.Json.JsonElement observation) : IOpenDesignConsoleReader
    {
        public Task<System.Text.Json.JsonElement> ReadAsync(PageConfigRecord page, bool windows, CancellationToken ct) => Task.FromResult(observation);
    }
    private sealed class ObservedCommandCode(CommandCodeUsageClient.AccountUsage usage) : ICommandCodeUsageClient
    {
        public Task<string?> FetchOrgIdAsync(string apiKey, CancellationToken ct) => Task.FromResult(usage.OrgId);
        public Task<CommandCodeUsageClient.AccountUsage?> FetchUsageAsync(string apiKey, CancellationToken ct) => Task.FromResult<CommandCodeUsageClient.AccountUsage?>(usage);
    }

    private sealed class FixtureReferences : IOfficialQuotaCatalogService
    {
        public ModelQuotaCatalog Catalog(string provider) => new(provider, DateTimeOffset.UtcNow, "fixture", [
            new(provider + ":deepseek v4.1 flash", "DeepSeek V4.1 Flash", "go", "Go", provider == "opendesign" ? 120m : 10m, false, "", provider == "opendesign" ? OfficialQuotaCatalogService.OpenDesignPricingUrl : OfficialQuotaCatalogService.CommandCodePlanUrl("go"))],
            ConversionRules: [new(provider + ":deepseek v4.1 flash", "go", provider == "opendesign" ? "go" : "individual-go-v1",
                provider == "opendesign" ? 2 : 1, provider == "opendesign" ? null : 10, DateTimeOffset.UtcNow.AddHours(1), "fixture")]);
        public Task<ModelQuotaCatalog> GetAsync(string provider, bool force, CancellationToken ct) => Task.FromResult(Catalog(provider));
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
