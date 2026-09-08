using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>DeepSeek 官方余额；保留所有币种，不把缺失金额或币种变成默认值。</summary>
public sealed class DeepSeekBalanceApiKeyMethod(HttpClient? http = null,
    Func<PageConfigRecord, CredentialClass, string?>? readSecret = null)
    : OfficialJsonQueryMethod("deepseek.balance.api-key", "deepseek", CredentialClass.ApiKey,
        CapabilityKind.BalanceOrQuota, ["api.deepseek.com"], ["", "/v1"], http, readSecret)
{
    protected override async Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        using var doc = await GetAsync(page, "/user/balance", key, ct);
        var balances = new List<CapabilityValue>();
        var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in doc.RootElement.GetProperty("balance_infos").EnumerateArray())
        {
            var currency = Text(row, "currency");
            if (!currencies.Add(currency)) throw Schema("DeepSeek 余额响应包含重复币种");
            balances.Add(Balance(page, Number(row, "total_balance"), currency));
        }
        return balances;
    }
}
