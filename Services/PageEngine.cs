using System.Windows.Threading;
using TokenUsageMonitorV3.Models;
using TokenUsageMonitorV3.Services.Adapters;

namespace TokenUsageMonitorV3.Services;

/// <summary>
/// 页面引擎（v4）：页面列表 → 适配器轮询 → 缓存 → 渲染当前页面到 MonitorState。
/// 全部页面后台轮询（30 分钟）+ 当前页面缓存渲染（切页秒显）+ 60s 探测。
/// </summary>
public sealed class PageEngine : IDisposable
{
    private readonly List<Page> _pages;
    private readonly MonitorState _state;
    private readonly AlertService _alerts;
    private readonly TrayIconService _tray;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, IPageAdapter> _adapters = new();
    private readonly Dictionary<string, PageData> _cache = new();
    private readonly ZCodeUsageService _zcode;
    // 适配器重建依赖（运行期新增页面时 SyncPages 用）
    private readonly OpenCodeUsageClient _oc;
    private readonly OpenCodeAuthService _auth;
    private readonly DeepSeekSessionService _dsSession;
    private readonly DeepSeekUsageClient _dsUsage;
    private readonly CommandCodeUsageClient _commandCode;
    private List<ZCodeUsageService.ProviderUsage> _lastDaily = new();

    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;
    private Task? _probeLoop;
    private string? _activeId;
    private ConnectionStatus _lastStatus = ConnectionStatus.Unknown;

    public event Action? StateChanged;

    /// <summary>需要登录（按当前页适配器类型分发）。ConsoleSession→DeepSeek 登录窗；WindowLimit→OAuth。</summary>
    public event Action<AdapterKind>? LoginRequired;

    /// <summary>当前激活页面（登录按钮按页面类型分发用）。</summary>
    public Page? ActivePage => _pages.FirstOrDefault(p => p.Id == _activeId);

    public PageEngine(
        List<Page> pages,
        MonitorState state,
        AlertService alerts,
        TrayIconService tray,
        AppSettings settings,
        Dispatcher dispatcher,
        OpenCodeUsageClient opencodeClient,
        OpenCodeAuthService auth,
        DeepSeekSessionService dsSession,
        DeepSeekUsageClient dsUsage,
        CommandCodeUsageClient commandCode,
        ZCodeUsageService zcode)
    {
        _pages = pages;
        _state = state;
        _alerts = alerts;
        _tray = tray;
        _settings = settings;
        _dispatcher = dispatcher;
        _zcode = zcode;
        _oc = opencodeClient;
        _auth = auth;
        _dsSession = dsSession;
        _dsUsage = dsUsage;
        _commandCode = commandCode;

        foreach (var p in pages)
            _adapters[p.Id] = CreateAdapter(p, opencodeClient, auth, dsSession, dsUsage, commandCode);
    }

    private static IPageAdapter CreateAdapter(Page p, OpenCodeUsageClient oc, OpenCodeAuthService auth,
        DeepSeekSessionService session, DeepSeekUsageClient dsUsage, CommandCodeUsageClient commandCode)
        => AdapterRegistry.Resolve(p.BaseUrl) switch
    {
        AdapterKind.WindowLimit => new WindowLimitAdapter(oc, auth),
        AdapterKind.CommandCode => new CommandCodeAdapter(commandCode),
        AdapterKind.ConsoleSession => new ConsoleSessionAdapter(session, dsUsage),
        AdapterKind.DeepSeekApi => new DeepSeekApiAdapter(session, dsUsage),
        _ => new ProbeAdapter(),
    };

    public void Start()
    {
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
        _probeLoop = Task.Run(() => ProbeLoopAsync(_cts.Token));
    }

    public void SetActivePage(string? pageId)
    {
        _activeId = pageId;
        var page = _pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null) return;

        _state.SetCurrentPage(page.Name, AdapterRegistry.Resolve(page.BaseUrl));
        RenderDailyUsage(_lastDaily);   // 切页即按新页 API key 重归属今日用量
        if (_cache.TryGetValue(page.Id, out var data))
            Render(page, data);
        else
        {
            // 首次切换无缓存：清空三窗口，避免残留上一页数据误导
            _state.Rolling.Update(-1, "", null, AlertLevel.None);
            _state.Weekly.Update(-1, "", null, AlertLevel.None);
            _state.Monthly.Update(-1, "", null, AlertLevel.None);
        }
    }

    public void SwitchToNext()
    {
        if (_pages.Count == 0) return;
        var idx = _pages.FindIndex(p => p.Id == _activeId);
        SetActivePage(_pages[(idx + 1) % _pages.Count].Id);
    }

    /// <summary>同步适配器表：面板新增/删除页面后调用（_adapters 仅构造时建，运行期新增页否则永不轮询）。</summary>
    public void SyncPages()
    {
        var current = _pages.Select(p => p.Id).ToHashSet();
        foreach (var p in _pages)
            _adapters.TryAdd(p.Id, CreateAdapter(p, _oc, _auth, _dsSession, _dsUsage, _commandCode));
        foreach (var id in _adapters.Keys.Where(k => !current.Contains(k)).ToList())
        {
            _adapters.Remove(id);
            _cache.Remove(id);
        }
    }

    public async Task RefreshNowAsync()
    {
        await PollAllAsync(CancellationToken.None);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        Logger.Log("page engine poll loop started");
        while (!ct.IsCancellationRequested)
        {
            try { await PollAllAsync(ct); }
            catch (Exception ex) { Logger.LogException("page engine poll", ex); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(10, _settings.PollIntervalMinutes)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        Logger.Log("page engine probe loop started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var active = _pages.FirstOrDefault(p => p.Id == _activeId);
                if (active is not null && _adapters.TryGetValue(active.Id, out var adapter))
                {
                    var (ok, error) = await adapter.ProbeAsync(active, ct);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (ok) return;
                        var isAuth = error.Contains("401") || error.Contains("会话失效") || error.Contains("未登录") || error.Contains("未配置");
                        if (isAuth)
                        {
                            _state.SetConnection(ConnectionStatus.AuthError, "需要登录/鉴权", error);
                            RaiseLoginRequired(AdapterRegistry.Resolve(active.BaseUrl));
                        }
                        else
                        {
                            _state.SetConnection(ConnectionStatus.Offline, "连接中断", error);
                            RaiseLoginRequired(AdapterRegistry.Resolve(active.BaseUrl));
                        }
                    });
                }
            }
            catch (Exception ex) { Logger.LogException("page engine probe", ex); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _settings.ProbeIntervalSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        foreach (var page in _pages)
        {
            if (!_adapters.TryGetValue(page.Id, out var adapter)) continue;
            try
            {
                var data = await adapter.FetchAsync(page, ct);
                _cache[page.Id] = data;
                if (page.Id == _activeId)
                    await _dispatcher.InvokeAsync(() => Render(page, data));
            }
            catch (Exception ex)
            {
                Logger.LogException($"page fetch {page.Name}", ex);
                // 失败时保留现有数据：渲染上次缓存（若有），状态如实显示连接中断
                await _dispatcher.InvokeAsync(() =>
                {
                    if (page.Id == _activeId && _cache.TryGetValue(page.Id, out var cached)) Render(page, cached);
                    _state.SetConnection(ConnectionStatus.Offline, "连接中断", ex.Message);
                });
            }
        }
        await RefreshDailyUsageAsync(ct);   // 每次轮询/手动刷新重算 zcode 今日用量
    }

    /// <summary>zcode 今日 token 消耗：本地 SQLite 扫描，按供应商归组后归属到当前页。</summary>
    private async Task RefreshDailyUsageAsync(CancellationToken ct)
    {
        try
        {
            var list = await _zcode.ComputeTodayByProviderAsync(ct);
            _lastDaily = list;
            await _dispatcher.InvokeAsync(() => RenderDailyUsage(list));
        }
        catch (Exception ex) { Logger.LogException("zcode daily usage", ex); }
    }

    /// <summary>归属规则：有 key 页按 provider.apiKey 匹配；无 key 页（控制台会话）按 baseURL 与页面同主域匹配。行名仅显示模型名。</summary>
    private void RenderDailyUsage(List<ZCodeUsageService.ProviderUsage> byProvider)
    {
        var page = ActivePage;
        if (page is null) { _state.SetDailyUsage(0, Array.Empty<(string, long)>()); return; }

        // DeepSeek 系页面已有官方用量数据，不再显示本地 zcode 统计
        var kind = AdapterRegistry.Resolve(page.BaseUrl);
        if (kind == AdapterKind.ConsoleSession || kind == AdapterKind.DeepSeekApi)
        {
            _state.SetDailyUsage(0, Array.Empty<(string, long)>());
            return;
        }

        string? pageKey = null;
        if (page.NeedsKey)
        {
            if (!CredentialStore.TryReadSecret(page.KeyTarget, out var k) || string.IsNullOrEmpty(k))
            {
                _state.SetDailyUsage(0, Array.Empty<(string, long)>());
                return;
            }
            pageKey = k;
        }
        var pageDomain = DomainOf(page.BaseUrl);

        var rows = new List<(string Model, long Tokens)>();
        foreach (var pu in byProvider)
        {
            var match = page.NeedsKey
                ? string.Equals(pu.Provider.ApiKey, pageKey, StringComparison.Ordinal)
                : DomainOf(pu.Provider.BaseUrl) is { } d && d == pageDomain;
            if (!match) continue;
            foreach (var m in pu.Models)
                rows.Add((m.Model, m.Tokens));   // 仅显示模型名（去掉供应商前缀，对齐参考布局）
        }
        _state.SetDailyUsage(rows.Sum(r => r.Tokens), rows);
    }

    /// <summary>取 URL 主域（末两段，如 platform.deepseek.com → deepseek.com）。</summary>
    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var parts = u.Host.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : u.Host;
    }

    /// <summary>把页面数据渲染到 MonitorState（按适配器类型显示对应布局）+ 页面级告警（金额/token 选填）。</summary>
    private void Render(Page page, PageData d)
    {
        // 金额/token 告警暂时停用（后续版本重新制定逻辑）——保留连接状态报警
        var pageLevel = AlertLevel.None;

        switch (AdapterRegistry.Resolve(page.BaseUrl))
        {
            case AdapterKind.WindowLimit:
            case AdapterKind.CommandCode:   // Command Code 套餐与 opencode 同构：5h/周/月 三窗口
                if (d.Rolling is { } r)
                    _state.Rolling.Update(r.Percent, r.Status, r.ResetsAt, AlertLevel.None);
                else
                    _state.Rolling.Update(-1, "", null, AlertLevel.None);   // 无数据清空，避免残留旧值
                if (d.Weekly is { } w)
                    _state.Weekly.Update(w.Percent, w.Status, w.ResetsAt, AlertLevel.None);
                else
                    _state.Weekly.Update(-1, "", null, AlertLevel.None);
                if (d.Monthly is { } m)
                    _state.Monthly.Update(m.Percent, m.Status, m.ResetsAt, AlertLevel.None);
                else
                    _state.Monthly.Update(-1, "", null, AlertLevel.None);
                var winLevel = _alerts.EvaluateWindows(_state);
                _tray.SetState(d.Status, winLevel > pageLevel ? winLevel : pageLevel);
                break;

            case AdapterKind.ConsoleSession:
            case AdapterKind.DeepSeekApi:
                long flashTokens = 0, proTokens = 0;
                decimal flashCost = 0, proCost = 0;
                foreach (var (model, tokens, cost) in d.ModelRows)
                {
                    if (model.Contains("flash", StringComparison.OrdinalIgnoreCase)) { flashTokens += tokens; flashCost += cost; }
                    else if (model.Contains("pro", StringComparison.OrdinalIgnoreCase)) { proTokens += tokens; proCost += cost; }
                }
                _state.DeepSeekFlash.Set(flashTokens, flashCost);
                _state.DeepSeekPro.Set(proTokens, proCost);
                _state.SetDeepSeekUsage(d.TotalTokens, d.TotalCost, flashTokens, flashCost, proTokens, proCost);
                if (AdapterRegistry.Resolve(page.BaseUrl) == AdapterKind.DeepSeekApi)
                    _state.SetBalance(d.BalanceCny, d.BalanceCurrency);   // 整合页同时显示官方余额
                _tray.SetState(d.Status, pageLevel > _alerts.LastWindowLevel ? pageLevel : _alerts.LastWindowLevel);
                break;

            default:
                _state.SetProbeModels(d.Models);
                _state.SetBalance(d.BalanceCny, d.BalanceCurrency);
                _tray.SetState(d.Status, pageLevel);
                break;
        }

        _state.SetLastSuccess(DateTimeOffset.UtcNow);
        _state.SetConnection(d.Status, d.StatusLabel, d.Error);
        RaiseLoginRequired(AdapterRegistry.Resolve(page.BaseUrl));
        StateChanged?.Invoke();
    }

    /// <summary>去抖：仅当状态由非 AuthError 转入 AuthError 时分发一次登录事件。</summary>
    private void RaiseLoginRequired(AdapterKind kind)
    {
        var was = _lastStatus;
        _lastStatus = _state.Connection;
        if (_state.Connection == ConnectionStatus.AuthError && was != ConnectionStatus.AuthError)
            LoginRequired?.Invoke(kind);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pollLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _probeLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
