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
}

/// <summary>
/// 页面引擎（生命周期层）：只管理页面列表、活动页、轮询节奏与 UI dispatcher。
/// 候选扫描、方法选择、能力查询、按能力回退和状态生成全部委托给 IPageRuntimeCoordinator。
/// </summary>
public sealed class PageEngine : IDisposable
{
    private readonly List<PageConfigRecord> _pages;
    private readonly MonitorState _state;
    private readonly AlertService _alerts;
    private readonly TrayIconService _tray;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly IPageRuntimeCoordinator _coordinator;

    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;
    private string? _activeId;
    private LoginKind _lastLoginKind = LoginKind.None;

    public event Action? StateChanged;

    /// <summary>需要登录（按候选凭据类别分发：ConsoleSession→DeepSeek 登录窗；OAuth→OpenCode 设备码）。</summary>
    public event Action<LoginKind>? LoginRequired;

    public PageConfigRecord? ActivePage => _pages.FirstOrDefault(p => p.Id == _activeId);

    public IReadOnlyList<PageConfigRecord> Pages => _pages;

    public PageEngine(
        List<PageConfigRecord> pages,
        MonitorState state,
        AlertService alerts,
        TrayIconService tray,
        AppSettings settings,
        SettingsStore settingsStore,
        Dispatcher dispatcher,
        IPageRuntimeCoordinator coordinator)
    {
        _pages = pages;
        _state = state;
        _alerts = alerts;
        _tray = tray;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        _coordinator = coordinator;
    }

    public void Start()
    {
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>切换活动页：立即持久化 ActivePageId，并触发一次快速刷新。</summary>
    public void SetActivePage(string? pageId, bool persist = true)
    {
        _activeId = pageId;
        if (persist && pageId is not null)
            _settingsStore.SaveActivePage(pageId);

        var page = _pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null)
        {
            _state.SetPageState(false, "");
            _state.ClearRuntime();
            StateChanged?.Invoke();
            return;
        }
        _state.SetPageState(true, page.Name);
        _ = RefreshPageSafeAsync(page, RefreshReason.Poll);
        StateChanged?.Invoke();
    }

    public void SwitchToNext()
    {
        if (_pages.Count == 0) return;
        var idx = _pages.FindIndex(p => p.Id == _activeId);
        SetActivePage(_pages[(idx + 1) % _pages.Count].Id);
    }

    /// <summary>手动强制刷新（重新扫描全部页面）。</summary>
    public async Task RefreshNowAsync()
    {
        await PollAllAsync(CancellationToken.None, manual: true);
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
    public void SetTemporaryOverride(string pageId, string? methodId)
    {
        _coordinator.SetTemporaryOverride(pageId, methodId);
        var page = _pages.FirstOrDefault(p => p.Id == pageId);
        if (page is not null) _ = RefreshPageSafeAsync(page, RefreshReason.Manual);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        Logger.Log("page engine poll loop started");
        while (!ct.IsCancellationRequested)
        {
            try { await PollAllAsync(ct, manual: false); }
            catch (Exception ex) { Logger.LogException("page engine poll", ex); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(10, _settings.PollIntervalMinutes)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollAllAsync(CancellationToken ct, bool manual)
    {
        foreach (var page in _pages)
            await RefreshPageSafeAsync(page, manual ? RefreshReason.Manual : RefreshReason.Poll);
    }

    private async Task RefreshPageSafeAsync(PageConfigRecord page, RefreshReason reason)
    {
        try { await RefreshPageAsync(page, reason, CancellationToken.None); }
        catch (Exception ex) { Logger.LogException($"refresh {page.Name}", ex); }
    }

    private async Task RefreshPageAsync(PageConfigRecord page, RefreshReason reason, CancellationToken ct)
    {
        var result = await _coordinator.RefreshAsync(page, reason, ct);
        // 非活动页的非扫描轮询结果不渲染到 UI（候选/快照保留在存储层）
        if (result.Scan is null && page.Id != _activeId) return;
        await _dispatcher.InvokeAsync(() => RenderRuntime(page, result));
    }

    private void RenderRuntime(PageConfigRecord page, PageRuntimeResult result)
    {
        _state.ApplySnapshot(result.Snapshot, _settings.ShowDailyTokens);
        _state.ApplyDiagnostics(page, result.Scan, result.Snapshot.Metadata.SelectedMethodId);

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

    public void Dispose()
    {
        _cts.Cancel();
        try { _pollLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
