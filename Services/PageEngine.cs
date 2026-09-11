using TokenConsumptionMonitoring.UI;
using System.Windows.Threading;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Runtime;

namespace TokenConsumptionMonitoring.Services;

/// <summary>需要登录的入口类型（由候选凭据类别推断；API key / 本地记录不弹登录窗）。</summary>
public enum LoginKind
{
    None,
    OpenCode,       // OAuth 会话（设备码）
    DeepSeekConsole,
    FireworksConsole,
}

/// <summary>
/// 页面引擎（生命周期层）：只管理页面列表、活动页、轮询节奏与 UI dispatcher。
/// 候选扫描、方法选择、能力查询、按能力回退和状态生成全部委托给 IPageRuntimeCoordinator。
/// </summary>
public sealed class PageEngine : IDisposable
{
    private readonly PageCatalog _catalog;
    private List<PageConfigRecord> _pages => _catalog.Snapshot();
    private readonly MonitorState _state;
    private readonly AlertService _alerts;
    private readonly ITrayStatusSink _tray;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly IPageRuntimeCoordinator _coordinator;
    private readonly PageRefreshQueue _refreshes;
    private readonly object _maintenanceGate = new();
    private readonly List<Task> _maintenance = new();

    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;
    private string? _activeId;
    private LoginKind _lastLoginKind = LoginKind.None;

    public event Action? StateChanged;

    /// <summary>活动页实际发生变化（切换/轮换/删除后回退），携带新活动页 Id；面板下拉框据此同步。</summary>
    public event Action<string?>? ActivePageChanged;

    /// <summary>需要登录（按候选凭据类别分发：ConsoleSession→DeepSeek 登录窗；OAuth→OpenCode 设备码）。</summary>
    public event Action<LoginKind>? LoginRequired;

    public PageConfigRecord? ActivePage => _pages.FirstOrDefault(p => p.Id == _activeId);

    public IReadOnlyList<PageConfigRecord> Pages => _pages;

    public PageEngine(
        PageCatalog catalog,
        MonitorState state,
        AlertService alerts,
        ITrayStatusSink tray,
        AppSettings settings,
        SettingsStore settingsStore,
        Dispatcher dispatcher,
        IPageRuntimeCoordinator coordinator)
    {
        _catalog = catalog;
        _state = state;
        _alerts = alerts;
        _tray = tray;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        _coordinator = coordinator;
        _refreshes = new PageRefreshQueue(catalog);
        catalog.Changed += OnCatalogChanged;
    }

    private void OnCatalogChanged(string pageId)
    {
        if (_catalog.Snapshot().Any(page => page.Id == pageId)) return;
        lock (_maintenanceGate)
        {
            _maintenance.RemoveAll(task => task.IsCompletedSuccessfully);
            _maintenance.Add(_coordinator.RemovePageAsync(pageId));
        }
    }

    public void Start()
    {
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>切换活动页：立即持久化 ActivePageId，并触发一次快速刷新。</summary>
    public void SetActivePage(string? pageId, bool persist = true)
    {
        var previousId = _activeId;
        _activeId = pageId;
        if (persist)
            _settingsStore.SaveActivePage(pageId);

        var page = _pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null)
        {
            _state.SetPageState(false, "");
            _state.ClearRuntime();
            _tray.SetState(_state.Connection, AlertLevel.None);
            StateChanged?.Invoke();
            if (previousId != pageId) ActivePageChanged?.Invoke(pageId);
            return;
        }
        _state.SetPageState(true, page.Name);
        if (_coordinator.TryGetSnapshot(page.Id, out var cached))
        {
            // 切页先投影该页已有状态，再等待网络刷新，避免把上一页的数值留在界面上。
            _state.ApplySnapshot(cached, _settings.ShowDailyTokens);
            if (_coordinator.TryGetScanReport(page.Id, out var cachedScan))
                _state.ApplyDiagnostics(page, cachedScan, cached.Metadata.SelectedMethodId);
            else
                _state.ApplyDiagnostics(page, null, cached.Metadata.SelectedMethodId);
            var cachedAlert = _alerts.EvaluateSnapshot(cached, page.Id);
            _tray.SetState(_state.Connection, cachedAlert.Overall);
        }
        else
        {
            _state.ClearRuntime();
            _tray.SetState(_state.Connection, AlertLevel.None);
            _state.ApplyDiagnostics(page, null, null);
        }
        _ = RefreshPageSafeAsync(page, RefreshReason.Poll);
        StateChanged?.Invoke();
        if (previousId != pageId) ActivePageChanged?.Invoke(pageId);
    }

    public void SwitchToNext()
    {
        var pages = _catalog.Snapshot();
        if (pages.Count == 0) return;
        var idx = pages.FindIndex(p => p.Id == _activeId);
        SetActivePage(pages[(idx + 1) % pages.Count].Id);
    }

    /// <summary>
    /// 激活页候选链中是否存在等待 OAuth 会话的候选（如 opencode.allowance.oauth）。
    /// 这类候选的凭据需求在方法级，与页面 API Key 无关：页面凭据已配置不代表无需登录。
    /// </summary>
    public bool ActivePageNeedsOAuthLogin()
    {
        var page = ActivePage;
        if (page is null || !_coordinator.TryGetScanReport(page.Id, out var report)) return false;
        return report.Candidates.Any(c =>
            c.Status == CandidateStatus.AuthRequired
            && c.Method.CredentialClass == CredentialClass.OAuthSession);
    }

    /// <summary>手动强制刷新（重新扫描全部页面）。</summary>
    public async Task RefreshNowAsync()
    {
        await PollAllAsync(_cts.Token, manual: true);
    }

    public async Task RefreshAfterSessionAsync(CredentialClass credentialClass)
    {
        _catalog.InvalidateSession(page => page.CredentialRef.ResolveClass() == credentialClass
            || credentialClass == CredentialClass.OAuthSession
                && page.EnabledCompatibilityMethods.Contains("opencode.allowance.oauth")
            || credentialClass == CredentialClass.ConsoleSession
                && (page.ParseProtocol() == KeyFormat.Protocol.DeepSeekConsole
                    || page.EnabledCompatibilityMethods.Contains("fireworks.balance.console")));
        await RefreshNowAsync();
    }

    /// <summary>保存/新建/删除后调用：对该页面执行一次完整重扫。</summary>
    public async Task RescanPageAsync(PageConfigRecord page, ScanReason reason)
    {
        await RefreshPageSafeAsync(page, reason switch
        {
            ScanReason.ConfigurationChanged => RefreshReason.ConfigurationChanged,
            ScanReason.Manual => RefreshReason.Manual,
            _ => RefreshReason.PageSaved,
        });
    }

    /// <summary>按 Id 查找页面并重扫（面板/托盘触发）。</summary>
    public async Task RescanById(string pageId, ScanReason reason)
    {
        var page = _pages.FirstOrDefault(p => p.Id == pageId);
        if (page is not null) await RescanPageAsync(page, reason);
    }

    /// <summary>临时覆盖自动选择（只作用于运行时；只刷新该页）。</summary>
    public Task SetTemporaryOverrideAsync(string pageId, string? methodId)
    {
        lock (_maintenanceGate)
        {
            if (_cts.IsCancellationRequested) return Task.CompletedTask;
            _maintenance.RemoveAll(task => task.IsCompletedSuccessfully);
            var task = ApplyOverrideAsync(pageId, methodId);
            _maintenance.Add(task);
            return task;
        }
    }

    private async Task ApplyOverrideAsync(string pageId, string? methodId)
    {
        try
        {
            await _coordinator.SetTemporaryOverrideAsync(pageId, methodId);
            var page = _pages.FirstOrDefault(p => p.Id == pageId);
            if (page is not null && !_cts.IsCancellationRequested)
                await RefreshPageSafeAsync(page, RefreshReason.Poll);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.LogException("set page override", ex); }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        Logger.Log("page engine poll loop started");
        while (!ct.IsCancellationRequested)
        {
            try { await PollAllAsync(ct, manual: false); }
            catch (Exception ex) { Logger.LogException("page engine poll", ex); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _settings.PollIntervalMinutes)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollAllAsync(CancellationToken ct, bool manual)
    {
        var reason = manual ? RefreshReason.Manual : RefreshReason.Poll;
        await Task.WhenAll(_pages.Select(page => RefreshPageSafeAsync(page, reason)));
    }

    private async Task RefreshPageSafeAsync(PageConfigRecord page, RefreshReason reason)
    {
        try { await _refreshes.Run(page, reason, token => RefreshPageAsync(page, reason, token)); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (OperationCanceledException) when (!_catalog.IsCurrent(page.Id, page.Revision)) { }
        catch (Exception ex) { Logger.LogException($"refresh {page.Name}", ex); }
    }

    private async Task RefreshPageAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
    {
        var result = await _coordinator.RefreshAsync(page, reason, ct);
        // 所有页面都可以刷新，但只有活动页能写入 MonitorState、托盘和浮窗。
        await _dispatcher.InvokeAsync(() =>
        {
            if (!ct.IsCancellationRequested && page.Id == _activeId && _catalog.IsCurrent(page.Id, page.Revision)) RenderRuntime(page, result);
        });
    }

    private void RenderRuntime(PageConfigRecord page, PageRuntimeResult result)
    {
        _state.ApplySnapshot(result.Snapshot, _settings.ShowDailyTokens);
        if (result.Scan is { } scan)
            _state.ApplyDiagnostics(page, scan, result.Snapshot.Metadata.SelectedMethodId);
        else if (_coordinator.TryGetScanReport(page.Id, out var cachedScan))
            _state.ApplyDiagnostics(page, cachedScan, result.Snapshot.Metadata.SelectedMethodId);
        else
            _state.ApplyDiagnostics(page, null, result.Snapshot.Metadata.SelectedMethodId);

        var alert = _alerts.EvaluateSnapshot(result.Snapshot, page.Id);
        // 窗口告警级别反映到 UI 进度条（Snapshot.Windows 与快照 Windows 顺序一致）
        for (var i = 0; i < _state.Snapshot.Windows.Count && i < alert.Windows.Count; i++)
            _state.Snapshot.Windows[i].UpdateLevel(alert.Windows[i].Level);

        _tray.SetState(_state.Connection, alert.Overall);
        RaiseLoginRequired(result);
        StateChanged?.Invoke();
    }

    /// <summary>去抖：仅当状态由非 AuthRequired 转入 AuthRequired 时分发一次登录事件；按凭据类别映射登录入口。</summary>
    private void RaiseLoginRequired(PageRuntimeResult result)
    {
        var kind = LoginKind.None;
        if (result.Snapshot.Status == SnapshotStatus.AuthRequired)
        {
            kind = result.AuthCredentialClass switch
            {
                CredentialClass.ConsoleSession when QueryMethods.FireworksConsoleBalanceMethod.Matches(ActivePage?.BaseUrl ?? "") => LoginKind.FireworksConsole,
                CredentialClass.ConsoleSession => LoginKind.DeepSeekConsole,
                CredentialClass.OAuthSession => LoginKind.OpenCode,
                _ => LoginKind.None,
            };
        }

        if (kind != LoginKind.None && _lastLoginKind != kind)
            LoginRequired?.Invoke(kind);
        _lastLoginKind = result.Snapshot.Status == SnapshotStatus.AuthRequired && kind != LoginKind.None
            ? kind : LoginKind.None;
    }

    public async Task StopAsync()
    {
        _catalog.Changed -= OnCatalogChanged;
        _cts.Cancel();
        await _refreshes.StopAsync();
        if (_pollLoop is not null) await _pollLoop;
        Task[] cleanup;
        lock (_maintenanceGate) cleanup = _maintenance.ToArray();
        await Task.WhenAll(cleanup);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
