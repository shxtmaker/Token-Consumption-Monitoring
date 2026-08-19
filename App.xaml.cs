using System.Windows;
using System.Windows.Threading;
using TokenUsageMonitorV3.Models;
using TokenUsageMonitorV3.Services;
using TokenUsageMonitorV3.UI;

namespace TokenUsageMonitorV3;

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

        _mutex = new Mutex(true, "TokenUsageMonitorV3_SingleInstance", out var createdNew);
        if (!createdNew) { Shutdown(); return; }

        var settingsStore = new SettingsStore();
        _settingsStoreField = settingsStore;
        _settings = settingsStore.Load();

        var state = new MonitorState();
        _state = state;
        state.SetShowDailyUsage(_settings.ShowDailyTokens);
        var opencode = new OpenCodeUsageClient();
        var oauth = new OAuthDeviceFlowClient();
        var openCodeAuth = new OpenCodeAuthService(oauth);   // 取代 MonitorService 的 opencode 职责
        _openCodeAuth = openCodeAuth;

        _tray = new TrayIconService();
        var alerts = new AlertService(_settings, msg => _tray.Balloon("额度告警", msg));

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

        // v4 页面模型：加载页面列表（默认空页），空态引导
        var pageStore = new Services.PageStore();
        var pages = pageStore.Load();

        // 页面引擎（v4 唯一引擎：轮询 + 探测 + 渲染）
        _pageEngine = new PageEngine(pages, state, alerts, _tray, _settings, Dispatcher,
            opencode, openCodeAuth, _deepSeekSession, deepSeekUsage,
            new CommandCodeUsageClient(), new ZCodeUsageService());

        _floating = new FloatingWindow { DataContext = state };
        _floating.SetLocked(_settings.WidgetLocked);   // 恢复锁定状态（置顶/禁拖动）
        _floating.SetBackgroundOpacity(_settings.WidgetOpacityPercent);   // 恢复背景透明度
        _panel = new MainPanel(pageStore, pages, state);   // 去 MonitorService，直接传 state
        _panel.PagesChanged += () =>
        {
            var has = pages.Count > 0;
            state.SetPageState(has, has ? pages[0].Name : "");
            _pageEngine.SetActivePage(has ? pages[0].Id : null);
        };
        _panel.PageSwitchRequested += id => _pageEngine.SetActivePage(id);
        if (pages.Count > 0) _panel.SetActivePageId(pages[0].Id);   // 面板下拉默认第一份配置（不沿用记忆）

        // 登录分发：由 PageEngine 事件驱动
        _pageEngine.LoginRequired += kind => Dispatcher.Invoke(() =>
        {
            if (kind == AdapterKind.ConsoleSession) ShowDeepSeekLogin();
            else if (kind == AdapterKind.WindowLimit) _ = LoginOpenCodeAsync();
        });

        WireEvents();

        if (_settings.AutoStart && !AutoStart.IsEnabled()) AutoStart.Set(true);
        if (!_settings.AutoStart && AutoStart.IsEnabled()) AutoStart.Set(false);

        openCodeAuth.TryLoadSession();   // 恢复 opencode 会话（若有）

        state.SetPageState(pages.Count > 0, pages.Count > 0 ? pages[0].Name : "");
        Services.Logger.Log($"pages loaded: {pages.Count} (active={_settings.ActivePageId ?? "(none)"})");
        if (pages.Count == 0)
        {
            Services.Logger.Log("no pages — empty state, guide to create");
            _tray.Balloon("创建你的第一个页面",
                "当前没有 API 配置页面。打开面板 → 新建页面，填写 API key / Base URL / 协议 / 模型列表。");
        }

        // 激活页面：设置记忆或第一个
        var activePage = pages.FirstOrDefault(p => p.Id == _settings.ActivePageId) ?? pages.FirstOrDefault();
        if (activePage is not null)
        {
            _settings.ActivePageId = activePage.Id;
            _pageEngine.SetActivePage(activePage.Id);
        }

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _countdownTimer.Tick += (_, _) =>
        {
            state.Rolling.UpdateCountdown();
            state.Weekly.UpdateCountdown();
            state.Monthly.UpdateCountdown();
        };
        _countdownTimer.Start();

        _pageEngine.Start(); // v4 页面引擎接管轮询与探测
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
    }

    private void ShowDeepSeekLogin()
    {
        _dsLoginWindow!.Owner = _floating;
        _dsLoginWindow.Show();
        _dsLoginWindow.Activate();
    }

    /// <summary>统一登录：根据当前页面 API 类型调用对应登录工具。</summary>
    private void LoginCurrentPage()
    {
        var page = _pageEngine?.ActivePage;
        if (page is null)
        {
            _tray!.Balloon("登录", "请先创建并选择页面（面板 → 添加模型供应商）。");
            return;
        }
        switch (AdapterRegistry.Resolve(page.BaseUrl))
        {
            case AdapterKind.ConsoleSession:
            case AdapterKind.DeepSeekApi:      // 整合页的官方用量同样依赖控制台会话
                ShowDeepSeekLogin();
                break;
            case AdapterKind.WindowLimit:
                _ = LoginOpenCodeAsync();
                break;
            default:
                _tray!.Balloon("登录", "该页面使用 API Key 认证（无需登录）；连接探测自动进行。");
                break;
        }
    }

    private async Task LoginOpenCodeAsync()
    {
        try { _tray!.Balloon("OpenCode", await _openCodeAuth!.LoginAsync()); }
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
            _settingsWindow.FloatingWidgetToggleRequested += show =>
            {
                if (show) _floating!.Show(); else _floating!.Hide();
            };
            _settingsWindow.DailyUsageToggleRequested += show => _state!.SetShowDailyUsage(show);
            _settingsWindow.OpacityChangeRequested += pct => _floating!.SetBackgroundOpacity(pct);
        }
        // 不挂在悬浮窗下：避免关闭桌面组件时连带隐藏设置窗
        if (_panel?.IsVisible == true) _settingsWindow.Owner = _panel;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ExitApp()
    {
        _countdownTimer?.Stop();
        _pageEngine?.Dispose();
        _deepSeekSession?.Dispose();
        _tray?.Dispose();
        if (_panel is not null) _panel.AllowClose = true;           // 放行复用窗口的真实关闭
        if (_dsLoginWindow is not null) _dsLoginWindow.AllowClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
