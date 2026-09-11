using System.Windows;
using System.Windows.Threading;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI;
using MessageBox = System.Windows.MessageBox;

namespace TokenConsumptionMonitoring;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private AppSettings? _settings;
    private MonitorState? _state;
    private SettingsStore? _settingsStoreField;
    private OpenCodeAuthService? _openCodeAuth;
    private PageEngine? _pageEngine;
    private DeepSeekSessionService? _deepSeekSession;
    private FireworksConsoleSession? _fireworksSession;
    private TrayIconService? _tray;
    private FloatingWindow? _floating;
    private MainPanel? _panel;
    private SettingsWindow? _settingsWindow;
    private DeepSeekLoginWindow? _dsLoginWindow;
    private DispatcherTimer? _countdownTimer;
    private readonly CancellationTokenSource _loginCts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 兜底：UI 线程异常写日志而非静默退出（小部件常驻，需可诊断）
        DispatcherUnhandledException += (_, args) =>
        {
            Services.Logger.LogException("dispatcher unhandled", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Services.Logger.LogException("appdomain unhandled",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.Logger.LogException("unobserved task", args.Exception);
            args.SetObserved();
        };

        _mutex = new Mutex(true, AppIdentity.MutexName, out var createdNew);
        if (!createdNew) { Shutdown(); return; }

        // 启动清扫：旧版遗留的 zcode 临时副本（%TEMP%\TokenConsumptionMonitoring\zcode_*.sqlite*）。
        // 线上化后不再产生新的本地库副本；残留后台清理（删除失败记日志并短重试，不拖慢启动、不阻断功能）。
        _ = Task.Run(() =>
        {
            try { new TempFileJanitor().CleanupLegacyZcodeCopies(); }
            catch (Exception ex) { Services.Logger.LogException("temp janitor startup", ex); }
        });

        var settingsStore = new SettingsStore();
        _settingsStoreField = settingsStore;
        _settings = settingsStore.Load();

        var state = new MonitorState();
        _state = state;

        _tray = new TrayIconService();
        var alerts = new AlertService(_settings, msg => _tray.Balloon("额度告警", msg));

        // 基础服务
        var opencode = new OpenCodeUsageClient();
        var oauth = new OAuthDeviceFlowClient();
        _openCodeAuth = new OpenCodeAuthService(oauth);

        // WebView2 登录窗（DeepSeek 会话宿主）：Show+Hide 触发 Loaded → 初始化 + 会话自检（cookie 持久，重启免登录）
        _dsLoginWindow = new DeepSeekLoginWindow(_deepSeekSession = new DeepSeekSessionService(Dispatcher));
        _dsLoginWindow.SessionReady += () =>
        {
            Services.Logger.Log("deepseek session ready");
            _pageEngine?.RefreshAfterSessionAsync(CredentialClass.ConsoleSession);
        };
        _dsLoginWindow.Show();
        _dsLoginWindow.Hide();
        var deepSeekUsage = new DeepSeekUsageClient(_deepSeekSession);
        var commandCode = new CommandCodeUsageClient();

        // 统一方法注册表 + 运行时协调器（扫描/选择/回退/缓存）
        _fireworksSession = new FireworksConsoleSession(Dispatcher);
        var registry = QueryMethodRegistry.BuildDefault(opencode, _openCodeAuth, _deepSeekSession, deepSeekUsage, commandCode, _fireworksSession);
        var fingerprints = new FingerprintBuilder(registry.Descriptors);


        // 页面配置：版本化 envelope；结构迁移可写回，恢复态保持只读
        var pageStore = new PageConfigStore();
        var loadResult = pageStore.Load();
        var document = loadResult.Document;
        if (document.IsCorrupt)
        {
            Services.Logger.Log($"pages 加载诊断：{document.Diagnostic}");
            _tray.Balloon("页面配置未加载", document.Diagnostic ?? "pages.json 无法读取");
        }
        else if (loadResult.RequiresSchemaRewrite)
        {
            Services.Logger.Log("pages schema 已迁移为当前 envelope");
            var saveResult = pageStore.Save(document, loadResult.WriteLease!);
            if (!saveResult.Succeeded)
                Services.Logger.Log($"pages schema 迁移写回失败：{saveResult.Diagnostic}");
        }
        var catalog = new PageCatalog(document.Pages);
        var pages = catalog.Snapshot();
        var coordinator = new PageRuntimeCoordinator(registry, fingerprints, new MethodStateStore(), new MethodResultCache(), catalog: catalog);

        // 页面引擎：只管理生命周期，委托 coordinator
        _pageEngine = new PageEngine(catalog, state, alerts, _tray, _settings, settingsStore, Dispatcher, coordinator);
        _openCodeAuth.TryLoadSession();   // 恢复 opencode OAuth 会话（OAuth 方法依赖登录状态）

        _floating = new FloatingWindow { DataContext = state };
        _floating.SetLocked(_settings.WidgetLocked);   // 恢复锁定状态（置顶/禁拖动）
        _floating.SetBackgroundOpacity(_settings.WidgetOpacityPercent);   // 恢复背景透明度
        _panel = new MainPanel(new PageConfigurationCommands(catalog, pageStore, new WindowsPageCredentialStore()),
            catalog, state, loadResult.IsRecoveryRequired ? loadResult.Diagnostic : null);

        WireEvents();

        // 活动页恢复：设置记忆或第一项；空态引导
        var activePage = pages.FirstOrDefault(p => p.Id == _settings.ActivePageId) ?? pages.FirstOrDefault();
        if (activePage is not null)
        {
            _panel.SetActivePageId(activePage.Id);
            _pageEngine.SetActivePage(activePage.Id);
            _ = _pageEngine.RescanPageAsync(activePage, ScanReason.Startup);
        }
        else
        {
            state.SetPageState(false, "");
            _tray.Balloon("创建你的第一个页面",
                "当前没有 API 配置页面。打开面板 → 新建页面，填写 API key / Base URL / 协议 / 模型列表，保存后自动扫描查询方法。");
        }

        if (_settings.AutoStart && !AutoStart.IsEnabled()) AutoStart.Set(true);
        if (!_settings.AutoStart && AutoStart.IsEnabled()) AutoStart.Set(false);

        Services.Logger.Log($"pages loaded: {pages.Count} (active={_settings.ActivePageId ?? "(none)"})");

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _countdownTimer.Tick += (_, _) => state.UpdateCountdowns();
        _countdownTimer.Start();

        _pageEngine.Start();
        _floating.Show();
        if (!_settings!.ShowFloatingWidget) _floating.Hide();   // 桌面组件开关：按设置隐藏悬浮窗
        Services.Logger.Log("app started");
        if (e.Args.Contains("--fireworks-login") && pages.FirstOrDefault(p => FireworksConsoleBalanceMethod.Matches(p.BaseUrl)) is { } fireworksPage)
            _ = ShowFireworksLoginAsync(fireworksPage.Id);
    }

    private void WireEvents()
    {
        _tray!.LeftClick += () => TogglePanel();
        _tray.RefreshRequested += () => _pageEngine?.RefreshNowAsync();
        _tray.OpenPanelRequested += () => TogglePanel(show: true);
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += ExitApp;

        _floating!.OpenPanelRequested += () => TogglePanel(show: true);
        _floating.AccountSwitchRequested += () => _pageEngine?.SwitchToNext();
        _floating.RefreshRequested += () => _pageEngine?.RefreshNowAsync();
        _floating.LockToggleRequested += locked =>
        {
            _settings!.WidgetLocked = locked;
            _settingsStoreField!.Save(_settings);
        };

        _panel!.RefreshRequested += () => _pageEngine?.RefreshNowAsync();
        _panel.LoginRequested += LoginCurrentPage;
        _panel.FireworksLoginRequested += id => _ = ShowFireworksLoginAsync(id);
        _panel.PageSwitchRequested += id => _pageEngine?.SetActivePage(id);
        // 悬浮窗/托盘等外部入口切换活动页时，面板下拉框跟随；表单编辑中不抢占（防覆盖未保存内容）
        _pageEngine!.ActivePageChanged += id =>
        {
            if (!_panel.IsEditingFormOpen) _panel.SetActivePageId(id);
        };
        _panel.PagesChanged += () =>
        {
            var engine = _pageEngine!;
            // 活动页不存在/被删除时选择当前排序第一项并修正保存（新建/编辑/删除不无条件重置到第一项）
            if (engine.ActivePage is null && engine.Pages.FirstOrDefault() is { } first)
                engine.SetActivePage(first.Id);
            else if (engine.ActivePage is null)
                engine.SetActivePage(null);
            else
                _state!.SetPageState(engine.ActivePage is not null, engine.ActivePage?.Name ?? "");
        };
        _panel.RescanRequested += async pageId =>
        {
            if (_pageEngine is { } engine)
                await engine.RescanById(pageId, ScanReason.Manual);
        };
        _panel.OverrideRequested += (pageId, methodId) => _pageEngine?.SetTemporaryOverrideAsync(pageId, methodId);

        // 登录分发：由候选凭据类别决定（ConsoleSession→DeepSeek 登录窗；OAuth→OpenCode 设备码）
        _pageEngine!.LoginRequired += kind =>
        {
            if (kind == LoginKind.DeepSeekConsole) ShowDeepSeekLogin();
            else if (kind == LoginKind.FireworksConsole && _pageEngine.ActivePage is { } page) _ = ShowFireworksLoginAsync(page.Id);
            else if (kind == LoginKind.OpenCode) _ = LoginOpenCodeAsync();
        };
    }

    private async Task ShowFireworksLoginAsync(string pageId)
    {
        var page = _pageEngine?.Pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null || _fireworksSession is null) return;
        try
        {
            await _fireworksSession.ShowLoginAsync(page, async () =>
            {
                var current = _pageEngine!.Pages.FirstOrDefault(p => p.Id == pageId);
                if (current is null) return "页面已删除，请重新选择页面。";
                var result = await new FireworksConsoleBalanceMethod(_fireworksSession).QueryAsync(current, null!, _loginCts.Token);
                if (result.Status != SnapshotStatus.Success) return result.Failure?.Reason ?? "尚未取得余额";
                await _pageEngine.RefreshAfterSessionAsync(CredentialClass.ConsoleSession);
                _pageEngine.SetActivePage(pageId);
                return null;
            });
        }
        catch (Exception) { MessageBox.Show("无法打开 Fireworks 登录窗口，请稍后重试。", "Fireworks 余额"); }
    }

    private void ShowDeepSeekLogin()
    {
        _dsLoginWindow!.Owner = _floating;
        _dsLoginWindow.Show();
        _dsLoginWindow.Activate();
    }

    /// <summary>统一登录：按面板表单当前协议分发（DeepSeek 控制台 → 会话登录窗；OAuth 候选待凭据 → 设备码；其余 → 面板内提示）。</summary>
    private void LoginCurrentPage(LoginKind kind)
    {
        Services.Logger.Log($"login current page: kind={kind}");
        switch (kind)
        {
            case LoginKind.DeepSeekConsole: ShowDeepSeekLogin(); break;
            case LoginKind.FireworksConsole:
                if (_pageEngine?.ActivePage is { } page) _ = ShowFireworksLoginAsync(page.Id);
                break;
            case LoginKind.OpenCode: _ = LoginOpenCodeAsync(); break;
            default:
                // 候选（如 opencode.allowance.oauth）等待 OAuth 会话时页面凭据虽是 API Key，仍需登录入口
                if (_pageEngine!.ActivePageNeedsOAuthLogin())
                {
                    Services.Logger.Log("login: pending oauth candidate → device flow");
                    _ = LoginOpenCodeAsync();
                    break;
                }
                // 托盘气泡常被系统通知设置折叠而不可见，点「登录」必须给面板内可见反馈
                MessageBox.Show("当前认证方式使用 API Key，无需登录；保存后自动扫描会探测连接与可用能力。",
                    "登录", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }

    private Task? _loginTask;
    private Task LoginOpenCodeAsync()
        => _loginTask is { IsCompleted: false } ? _loginTask : _loginTask = LoginOpenCodeCoreAsync();

    private async Task LoginOpenCodeCoreAsync()
    {
        try
        {
            _tray!.Balloon("OpenCode", await _openCodeAuth!.LoginAsync(_loginCts.Token));
            await _pageEngine!.RefreshAfterSessionAsync(CredentialClass.OAuthSession);   // 会话已变化：立即重扫，候选链的“需要凭据/权限”随之更新
        }
        catch (Exception ex) { _tray!.Balloon("OpenCode 登录失败", ex.Message); }
    }

    private void TogglePanel(bool? show = null)
    {
        if (_panel is null) return;
        var wantShow = show ?? !_panel.IsVisible;
        if (wantShow)
        {
            _panel.Owner = _floating;
            _panel.Show();
            _panel.Activate();
        }
        else
        {
            _panel.Hide();
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings!, _settingsStoreField!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.FloatingWidgetToggleRequested += show => { if (show) _floating!.Show(); else _floating!.Hide(); };
            _settingsWindow.DailyUsageToggleRequested += _ => _pageEngine?.RefreshNowAsync();
            _settingsWindow.OpacityChangeRequested += pct => _floating!.SetBackgroundOpacity(pct);
        }
        if (_panel?.IsVisible == true) _settingsWindow.Owner = _panel;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private bool _exiting;
    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _countdownTimer?.Stop();
        _loginCts.Cancel();
        if (_loginTask is not null) await _loginTask;
        if (_pageEngine is not null) await _pageEngine.StopAsync();
        _pageEngine?.Dispose();
        _deepSeekSession?.Dispose();
        _fireworksSession?.Dispose();
        _tray?.Dispose();
        if (_panel is not null) _panel.AllowClose = true;           // 放行复用窗口的真实关闭
        if (_dsLoginWindow is not null) _dsLoginWindow.AllowClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loginCts.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
