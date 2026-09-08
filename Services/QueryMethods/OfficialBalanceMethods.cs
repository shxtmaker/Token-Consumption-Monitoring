using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>Moonshot 国内与国际账户的官方可用余额；两个地域不共享币种或密钥。</summary>
public sealed class MoonshotBalanceMethod(HttpClient? http = null,
    Func<PageConfigRecord, CredentialClass, string?>? readSecret = null)
    : OfficialJsonQueryMethod("moonshot.balance.api-key", "moonshot", CredentialClass.ApiKey, CapabilityKind.BalanceOrQuota,
        ["api.moonshot.cn", "api.moonshot.ai"], ["", "/v1"], http, readSecret)
{
    protected override async Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        using var doc = await GetAsync(page, "/v1/users/me/balance", key, ct);
        var root = doc.RootElement;
        if (Number(root, "code") != 0 || !root.GetProperty("status").GetBoolean())
            throw Schema("Moonshot 余额业务响应未成功");
        var available = Number(root.GetProperty("data"), "available_balance");
        return [Balance(page, available, new Uri(page.BaseUrl).Host == "api.moonshot.cn" ? "CNY" : "USD")];
    }
}

/// <summary>OpenRouter Key 额度和今日报告费用分别查询，终身消费不与周期额度混用。</summary>
public sealed class OpenRouterKeyMethod(bool cost = false, HttpClient? http = null,
    Func<PageConfigRecord, CredentialClass, string?>? readSecret = null)
    : OfficialJsonQueryMethod(cost ? "openrouter.daily-cost.api-key" : "openrouter.key-quota.api-key", "openrouter",
        CredentialClass.ApiKey, cost ? CapabilityKind.ReportedCost : CapabilityKind.BalanceOrQuota,
        ["openrouter.ai"], ["", "/api", "/api/v1"], http, readSecret)
{
    protected override async Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        using var doc = await GetAsync(page, "/api/v1/key", key, ct);
        var data = doc.RootElement.GetProperty("data");
        if (cost)
        {
            var amount = OptionalNumber(data, "usage_daily");
            if (amount is null) return [];
            var now = DateTimeOffset.UtcNow;
            return [new ReportedCostValue(CapabilityKind.ReportedCost, Identity(page), Scope(page),
                new Coverage(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), now, Granularity.PerDay, "当前 Key · UTC 今日"),
                now, 1, false, false, amount.Value, "USD", now.AddMinutes(5))];
        }
        // 公开契约明确允许无限额度为 null。必须保留未知，不能伪装成 0 余额。
        if (!data.TryGetProperty("limit", out _) || !data.TryGetProperty("limit_remaining", out _))
            throw Schema("OpenRouter 响应缺少额度字段");
        var limit = OptionalNumber(data, "limit");
        var remaining = OptionalNumber(data, "limit_remaining");
        if (limit is null && remaining is null) return [];
        if (limit < 0) throw Schema("OpenRouter 额度上限无效");
        var reset = data.TryGetProperty("limit_reset", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String
            ? r.GetString() : null;
        var period = reset switch { "daily" => "每日", "weekly" => "每周", "monthly" => "每月", null => "无周期重置", _ => "服务方定义的周期" };
        return [Balance(page, null, "USD", limit is not null && remaining is not null ? limit - remaining : null,
            limit, remaining, new Coverage(null, null, Granularity.Unknown, $"当前 Key · {period}"))];
    }
}

/// <summary>管理密钥查询账户 credits；余额严格由服务方报告的总 credits 和总使用额相减。</summary>
public sealed class OpenRouterCreditsMethod(HttpClient? http = null,
    Func<PageConfigRecord, CredentialClass, string?>? readSecret = null)
    : OfficialJsonQueryMethod("openrouter.credits.management-key", "openrouter", CredentialClass.ManagementKey,
        CapabilityKind.BalanceOrQuota, ["openrouter.ai"], ["", "/api", "/api/v1"], http, readSecret)
{
    protected override async Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        using var doc = await GetAsync(page, "/api/v1/credits", key, ct);
        var data = doc.RootElement.GetProperty("data");
        var credits = Number(data, "total_credits");
        var used = Number(data, "total_usage");
        return [Balance(page, credits - used, "USD")];
    }
}
