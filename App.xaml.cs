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
            _pageEngine?.RefreshNowAsync();
        };
        _dsLoginWindow.Show();
        _dsLoginWindow.Hide();
        var deepSeekUsage = new DeepSeekUsageClient(_deepSeekSession);
        var zcode = new ZCodeUsageService();
        var commandCode = new CommandCodeUsageClient();

        // 统一方法注册表 + 运行时协调器（扫描/选择/回退/缓存）
        var registry = QueryMethodRegistry.BuildDefault(opencode, _openCodeAuth, _deepSeekSession, deepSeekUsage, zcode, commandCode);
        var fingerprints = new FingerprintBuilder(registry.Descriptors);
        var coordinator = new PageRuntimeCoordinator(registry, fingerprints, new MethodStateStore(), new MethodResultCache(), zcode);

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
        var pages = document.Pages;

        // 页面引擎：只管理生命周期，委托 coordinator
        _pageEngine = new PageEngine(pages, state, alerts, _tray, _settings, settingsStore, Dispatcher, coordinator);
        _openCodeAuth.TryLoadSession();   // 恢复 opencode OAuth 会话（OAuth 方法依赖登录状态）

        _floating = new FloatingWindow { DataContext = state };
        _floating.SetLocked(_settings.WidgetLocked);   // 恢复锁定状态（置顶/禁拖动）
        _floating.SetBackgroundOpacity(_settings.WidgetOpacityPercent);   // 恢复背景透明度
        _panel = new MainPanel(pageStore, pages, state);

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
        _panel.PageSwitchRequested += id => _pageEngine?.SetActivePage(id);
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
        _panel.OverrideRequested += (pageId, methodId) => _pageEngine?.SetTemporaryOverride(pageId, methodId);

        // 登录分发：由候选凭据类别决定（ConsoleSession→DeepSeek 登录窗；OAuth→OpenCode 设备码）
        _pageEngine!.LoginRequired += kind =>
        {
            if (kind == LoginKind.DeepSeekConsole) ShowDeepSeekLogin();
            else if (kind == LoginKind.OpenCode) _ = LoginOpenCodeAsync();
        };
    }

    private void ShowDeepSeekLogin()
    {
        _dsLoginWindow!.Owner = _floating;
        _dsLoginWindow.Show();
        _dsLoginWindow.Activate();
    }

    /// <summary>统一登录：根据当前页面状态调用对应登录入口。</summary>
    private void LoginCurrentPage()
    {
        var page = _pageEngine?.ActivePage;
        if (page is null)
        {
            _tray!.Balloon("登录", "请先创建并选择页面（面板 → 新建）。");
            return;
        }
        var kind = page.CredentialRef.ResolveClass() switch
        {
            CredentialClass.ConsoleSession => LoginKind.DeepSeekConsole,
            CredentialClass.OAuthSession => LoginKind.OpenCode,
            _ => LoginKind.None,
        };
        switch (kind)
        {
            case LoginKind.DeepSeekConsole: ShowDeepSeekLogin(); break;
            case LoginKind.OpenCode: _ = LoginOpenCodeAsync(); break;
            default:
                _tray!.Balloon("登录", "该页面使用 API Key 认证（无需登录）；自动扫描会探测连接与可用能力。");
                break;
        }
    }

    private async Task LoginOpenCodeAsync()
    {
        try { _tray!.Balloon("OpenCode", await _openCodeAuth!.LoginAsync(_loginCts.Token)); }
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

    private void ExitApp()
    {
        _countdownTimer?.Stop();
        _loginCts.Cancel();
        _pageEngine?.Dispose();
        _deepSeekSession?.Dispose();
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
