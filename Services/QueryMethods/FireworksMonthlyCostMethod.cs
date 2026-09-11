using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>读取 Fireworks 账户月度消费，不将消费预算或预算余量当作充值余额。</summary>
public sealed class FireworksMonthlyCostMethod(HttpClient? http = null,
    Func<PageConfigRecord, CredentialClass, string?>? readSecret = null)
    : OfficialJsonQueryMethod("fireworks.monthly-cost.api-key", "fireworks", CredentialClass.ApiKey,
        CapabilityKind.ReportedCost, ["api.fireworks.ai"], ["", "/v1", "/inference/v1"], http, readSecret)
{
    protected override async Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        var values = new List<CapabilityValue>();
        var seenAccounts = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        for (var batch = 0; batch < 20; batch++)
        {
            var path = "/v1/accounts?pageSize=200" + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token));
            using var accounts = await GetAsync(page, path, key, ct);
            foreach (var account in accounts.RootElement.GetProperty("accounts").EnumerateArray())
            {
                var name = Text(account, "name");
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\Aaccounts/[a-zA-Z0-9_-]+\z"))
                    throw Schema("Fireworks 账户资源名称无效");
                if (!seenAccounts.Add(name)) continue;
                using var quota = await GetAsync(page, "/v1/" + name + "/quotas/monthly-spend-usd", key, ct);
                var used = Number(quota.RootElement, "usage");
                if (used < 0) throw Schema("Fireworks 月度消费不能为负数");
                var now = DateTimeOffset.UtcNow;
                // 服务方定义月度周期；不在未声明时区时自行推算重置时间。
                var source = Identity(page) with { Account = name, Scope = $"api-key:page:{page.Id}:account:{name}" };
                values.Add(new ReportedCostValue(CapabilityKind.ReportedCost, source,
                    Scope(page) with { Account = name },
                    new Coverage(null, null, Granularity.PerWindow, "账户本月消费"),
                    now, 1, false, false, used, "USD", now.AddMinutes(5)));
            }
            token = accounts.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(token)) return values;
            if (!seenTokens.Add(token)) throw Schema("Fireworks 账户分页重复");
        }
        throw Schema("Fireworks 账户分页超出读取上限");
    }
}

