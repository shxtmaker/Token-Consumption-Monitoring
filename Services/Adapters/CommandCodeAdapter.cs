using TokenUsageMonitorV3.Models;

namespace TokenUsageMonitorV3.Services.Adapters;

/// <summary>
/// Command Code 适配器：GOAT 等订阅套餐的用量监控。
/// 数据来自官方 /alpha 控制面（Bearer key，Studio API key 或 CLI 登录的 apiKey）：
/// - 5h/周 滚动窗口（windowLimits.fiveHour|weekly，used/cap/resetAt）
/// - 月度：套餐总额（GOAT=$70）+ 计费周期消费（usage/summary）+ 周期结束重置
/// 用量与 opencode 网关同构展示（悬浮窗 5h/周/月 三窗口）。
/// </summary>
public sealed class CommandCodeAdapter : IPageAdapter
{
    private readonly CommandCodeUsageClient _client;

    public CommandCodeAdapter(CommandCodeUsageClient client) => _client = client;

    public AdapterKind Kind => AdapterKind.CommandCode;

    public async Task<PageData> FetchAsync(Page page, CancellationToken ct)
    {
        var key = ResolveKey(page);
        if (key is null)
            return new PageData
            {
                Status = ConnectionStatus.AuthError,
                StatusLabel = "未配置 key",
                Error = "请填写 Command Code API key（Studio → API keys），或先用 CLI 登录使 ~/.commandcode/auth.json 可用",
            };

        try
        {
            var usage = await _client.FetchUsageAsync(key, ct);
            if (usage is null)
                return new PageData { Status = ConnectionStatus.Offline, StatusLabel = "获取失败", Error = "无用量数据" };

            var plan = usage.Plan;
            var planName = plan?.Name ?? "Command Code";
            var monthlyLimitUsd = plan?.MonthlyCredits;
            var totalCost = usage.TotalCost;

            // 5h / 周窗口：百分比 + 绝对额度（美元 → microCents）
            (int Percent, string Status, DateTimeOffset? ResetsAt)? rolling = null, weekly = null;
            (long? L, long? R)? rollingAbs = null, weeklyAbs = null;
            var fh = usage.Credits?.Limits?.FiveHour;
            if (fh is not null && fh.Cap > 0)
            {
                rolling = (fh.Percent, "正常", fh.ResetAt);
                rollingAbs = (fh.LimitMicroCents, fh.RemainingMicroCents);
            }
            var wk = usage.Credits?.Limits?.Weekly;
            if (wk is not null && wk.Cap > 0)
            {
                weekly = (wk.Percent, "正常", wk.ResetAt);
                weeklyAbs = (wk.LimitMicroCents, wk.RemainingMicroCents);
            }

            // 月度：套餐总额 vs 计费周期消费；未知套餐时不显示月度窗口
            (int Percent, string Status, DateTimeOffset? ResetsAt)? monthly = null;
            (long? L, long? R)? monthlyAbs = null;
            if (monthlyLimitUsd is { } limit)
            {
                var usedPct = (int)Math.Round(Math.Min(1, totalCost / limit) * 100);
                monthly = (usedPct, "正常", usage.Subscription?.CurrentPeriodEnd);
                monthlyAbs = (ToMicro(limit), ToMicro(Math.Max(0, limit - totalCost)));
            }

            if (rolling is null && weekly is null && monthly is null)
                return new PageData { Status = ConnectionStatus.Offline, StatusLabel = "获取失败", Error = "未解析到窗口用量" };

            Logger.Log($"commandcode {planName}: 月剩 ${usage.Credits?.MonthlyCredits ?? 0:0.00} " +
                       $"5h={rolling?.Percent}% 周={weekly?.Percent}% 月={monthly?.Percent}% " +
                       $"5h重置={fh?.ResetAt:HH:mm} 周重置={wk?.ResetAt:MM-dd HH:mm} 月底={usage.Subscription?.CurrentPeriodEnd:MM-dd}");

            return new PageData
            {
                Status = ConnectionStatus.Ok,
                StatusLabel = planName + (monthlyLimitUsd is { } ml ? $"套餐 · 月 ${ml:0.##}" : "套餐"),
                Rolling = rolling,
                Weekly = weekly,
                Monthly = monthly,
                RollingAbsolute = rollingAbs,
                WeeklyAbsolute = weeklyAbs,
                MonthlyAbsolute = monthlyAbs,
            };
        }
        catch (InvalidOperationException ex)
        {
            var isAuth = ex.Message.Contains("401") || ex.Message.Contains("UNAUTHORIZED");
            return new PageData
            {
                Status = isAuth ? ConnectionStatus.AuthError : ConnectionStatus.Offline,
                StatusLabel = isAuth ? "鉴权失败" : "获取失败",
                Error = ex.Message,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Logger.LogException("commandcode fetch", ex);
            return new PageData { Status = ConnectionStatus.Offline, StatusLabel = "连接中断", Error = "超时/网络错误" };
        }
    }

    public Task<(bool Ok, string Error)> ProbeAsync(Page page, CancellationToken ct)
    {
        var key = ResolveKey(page);
        if (key is null) return Task.FromResult((false, "未配置 key"));
        return ProbeCoreAsync(key, ct);
    }

    private async Task<(bool Ok, string Error)> ProbeCoreAsync(string key, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await _client.FetchOrgIdAsync(key, cts.Token);   // 无 key 不可达，且 401 即 key 无效
            return (true, "");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("401") || ex.Message.Contains("UNAUTHORIZED"))
        {
            return (false, "401 key 无效");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (false, "超时/网络错误");
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>key 优先级：页面凭据（Windows 凭据管理器）→ 本地 CLI 登录 apiKey。</summary>
    private static string? ResolveKey(Page page)
        => CredentialStore.TryReadSecret(page.KeyTarget, out var k) && !string.IsNullOrWhiteSpace(k)
            ? k
            : CommandCodeUsageClient.ReadLocalApiKey();

    private static long ToMicro(double usd) => (long)Math.Round(Math.Max(0, usd) * 10_000_000);
}
