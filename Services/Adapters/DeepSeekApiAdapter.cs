using System.Text.Json;
using TokenUsageMonitorV3.Models;

namespace TokenUsageMonitorV3.Services.Adapters;

/// <summary>
/// DeepSeek 官方 API 适配器（api.deepseek.com + API key）：
/// - 连接探测：GET /models
/// - 官方余额：GET /user/balance
/// - 官方用量：整合控制台会话（全局资源，登录一次全应用复用）按模型拆分今日 token + 金额；
///   官方 API 无用量端点，此为唯一官方口径来源。模型列表显示已取消。
/// </summary>
public sealed class DeepSeekApiAdapter : IPageAdapter
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly DeepSeekSessionService _session;
    private readonly DeepSeekUsageClient _usage;

    public DeepSeekApiAdapter(DeepSeekSessionService session, DeepSeekUsageClient usage)
    {
        _session = session;
        _usage = usage;
    }

    public AdapterKind Kind => AdapterKind.DeepSeekApi;

    public async Task<PageData> FetchAsync(Page page, CancellationToken ct)
    {
        var (ok, error) = await ProbeAsync(page, ct);
        if (!ok)
            return new PageData { Status = ConnectionStatus.Offline, StatusLabel = "连接失败", Error = error };

        var (balance, currency) = await FetchBalanceAsync(page, ct);

        // 官方用量：控制台会话已登录才可用（按模型拆分今日 token + 金额）
        long totalTokens = 0;
        decimal totalCost = 0;
        var rows = new List<(string Model, long Tokens, decimal Cost)>();
        if (_session.IsLoggedIn)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var todayStart = new DateTimeOffset(now.Date, TimeZoneInfo.Local.GetUtcOffset(now));
                var tzSec = (int)TimeZoneInfo.Local.BaseUtcOffset.TotalSeconds;
                var byModel = await _usage.FetchTodayByModelAsync(
                    todayStart.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), tzSec);
                foreach (var (model, u) in byModel)
                {
                    var tokens = u.CacheHitTokens + u.CacheMissTokens + u.ResponseTokens;
                    totalTokens += tokens;
                    totalCost += u.CostCny;
                    rows.Add((model, tokens, u.CostCny));
                }
            }
            catch (Exception ex)
            {
                Logger.LogException("deepseek api usage", ex);
            }
        }

        return new PageData
        {
            Status = _session.IsLoggedIn ? ConnectionStatus.Ok : ConnectionStatus.Warn,
            StatusLabel = _session.IsLoggedIn ? "正常" : "控制台未登录",
            Error = _session.IsLoggedIn ? "" : "官方用量需控制台会话：托盘 → 登录",
            BalanceCny = balance,
            BalanceCurrency = currency,
            TotalTokens = totalTokens,
            TotalCost = totalCost,
            CostCurrency = "CNY",
            ModelRows = rows,
        };
    }

    public async Task<(bool Ok, string Error)> ProbeAsync(Page page, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(page, "/models"));
            ApplyAuth(request, page);
            using var response = await _http.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode) return (true, "");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (false, "401 密钥无效");
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return (false, "403 无权限");
            return (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (false, "超时/网络错误");
        }
    }

    /// <summary>官方余额：/user/balance → balance_infos[0].total_balance（CNY）。</summary>
    private async Task<(decimal? Balance, string Currency)> FetchBalanceAsync(Page page, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(page, "/user/balance"));
            ApplyAuth(request, page);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (null, "");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("balance_infos", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (null, "");
            foreach (var b in arr.EnumerateArray())
            {
                if (!b.TryGetProperty("total_balance", out var tb)) continue;
                var text = tb.ValueKind == JsonValueKind.String ? tb.GetString() : tb.GetRawText();
                if (decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    var currency = b.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString()! : "CNY";
                    return (v, currency);
                }
            }
        }
        catch (Exception ex) { Logger.LogException("deepseek api balance", ex); }
        return (null, "");
    }

    private static string BuildUrl(Page page, string path) => page.BaseUrl.TrimEnd('/') + path;

    private static void ApplyAuth(HttpRequestMessage request, Page page)
    {
        var key = CredentialStore.TryReadSecret(page.KeyTarget, out var k) ? k : null;
        if (!string.IsNullOrEmpty(key))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
    }
}
