using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services.Adapters;

/// <summary>
/// DeepSeek 控制台适配器：官方会话用量（WebView2 页面捕获 amount+cost，flash/pro 拆分 + 金额）。
/// 会话为全局资源（登录一次，所有 deepseek 页面复用）。
/// </summary>
public sealed class ConsoleSessionAdapter : IPageAdapter
{
    private readonly DeepSeekSessionService _session;
    private readonly DeepSeekUsageClient _usage;

    public ConsoleSessionAdapter(DeepSeekSessionService session, DeepSeekUsageClient usage)
    {
        _session = session;
        _usage = usage;
    }

    public AdapterKind Kind => AdapterKind.ConsoleSession;

    public async Task<PageData> FetchAsync(Page page, CancellationToken ct)
    {
        if (!_session.IsLoggedIn)
            return new PageData { Status = ConnectionStatus.AuthError, StatusLabel = "未登录控制台", Error = "请登录 DeepSeek 控制台" };

        try
        {
            var now = DateTimeOffset.Now;
            var todayStart = new DateTimeOffset(now.Date, TimeZoneInfo.Local.GetUtcOffset(now));
            var tzSec = (int)TimeZoneInfo.Local.BaseUtcOffset.TotalSeconds;

            var byModel = await _usage.FetchTodayByModelAsync(
                todayStart.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), tzSec);

            long totalTokens = 0;
            decimal totalCost = 0;
            var rows = new List<(string Model, long Tokens, decimal Cost)>();
            foreach (var (model, u) in byModel)
            {
                var tokens = u.CacheHitTokens + u.CacheMissTokens + u.ResponseTokens;
                totalTokens += tokens;
                totalCost += u.CostCny;
                rows.Add((model, tokens, u.CostCny));
            }

            return new PageData
            {
                Status = ConnectionStatus.Ok,
                StatusLabel = "正常",
                TotalTokens = totalTokens,
                TotalCost = totalCost,
                CostCurrency = "CNY",
                ModelRows = rows,
            };
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogException("deepseek adapter (auth)", ex);
            return new PageData { Status = ConnectionStatus.AuthError, StatusLabel = "会话失效", Error = ex.Message };
        }
        catch (Exception ex)
        {
            Logger.LogException("deepseek adapter", ex);
            return new PageData { Status = ConnectionStatus.Offline, StatusLabel = "获取失败", Error = ex.Message };
        }
    }

    public async Task<(bool Ok, string Error)> ProbeAsync(Page page, CancellationToken ct)
    {
        var ok = await _session.CheckSessionAsync();
        return ok ? (true, "") : (false, "会话失效");
    }
}
