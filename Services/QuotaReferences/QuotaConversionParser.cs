using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services.QuotaReferences;

/// <summary>仅解析官网公开数据字面量，不执行下载的 JavaScript。</summary>
public static class QuotaConversionParser
{
    private static MatchCollection Matches(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static string Asset(string text, string pattern)
    {
        var paths = Matches(text, pattern).Select(m => m.Value).Distinct().ToArray();
        return paths.Length == 1 ? paths[0] : throw new InvalidDataException("官网定价资源不唯一");
    }

    public static IReadOnlyList<ModelQuotaConversionRule> OpenDesign(string script,
        IReadOnlyList<ModelQuotaReference> entries, DateTimeOffset now)
    {
        var multipliers = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (Match match in Matches(script, "name:\"(?<name>[^\"]{1,160})\",icon:[^,{}]{1,100},(?:iconClass:\"[^\"]{1,100}\",)?multiplier:(?<factor>[0-9.]+),weeklyRequests:"))
        {
            var id = OfficialQuotaParser.ModelId("opendesign", match.Groups["name"].Value);
            if (!decimal.TryParse(match.Groups["factor"].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var factor) || factor <= 0 || factor > 1000
                || !multipliers.TryAdd(id, factor)) throw new InvalidDataException("官网模型倍率无效");
        }
        if (multipliers.Count == 0) throw new InvalidDataException("官网模型倍率缺失");
        var result = new List<ModelQuotaConversionRule>();
        foreach (var group in entries.Where(e => !e.IsFree).GroupBy(e => e.PlanId))
        {
            var rows = group.ToArray();
            if (rows.Any(e => !multipliers.ContainsKey(e.ModelId))
                || rows.Select(e => e.MonthlyUsd * multipliers[e.ModelId]).Distinct().Count() != 1)
                continue; // 不拼接不一致的官网快照。
            result.AddRange(rows.Select(e => new ModelQuotaConversionRule(e.ModelId, e.PlanId, e.PlanId,
                1m / multipliers[e.ModelId], null, now.AddHours(24), "https://open-design.ai/model-credits/")));
        }
        return result;
    }

    public static IReadOnlyList<ModelQuotaConversionRule> CommandCode(string html, string tiers,
        IReadOnlyList<ModelQuotaReference> entries, DateTimeOffset now)
    {
        var flight = new StringBuilder();
        foreach (Match match in Matches(html, "self\\.__next_f\\.push\\(\\[1,(?<json>\"(?:[^\"\\\\]|\\\\.)*\")\\]\\)"))
            flight.Append(JsonSerializer.Deserialize<string>(match.Groups["json"].Value));
        var text = flight.ToString();
        var marker = "\"models\":[";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0 || text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("官网模型预算缺失或不唯一");
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[(start + marker.Length - 1)..]));
        using var document = JsonDocument.ParseValue(ref reader);
        var models = document.RootElement.EnumerateArray().ToArray();
        if (models.Length is 0 or > 1000) throw new InvalidDataException("官网模型预算数量无效");
        var result = new List<ModelQuotaConversionRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var id = OfficialQuotaParser.ModelId("commandcode", model.GetProperty("name").GetString()!);
            if (!seen.Add(id)) throw new InvalidDataException("官网模型名称不唯一");
            var effective = model;
            var expires = now.AddHours(24);
            if (model.TryGetProperty("planBudgetSteps", out var steps))
            {
                var ordered = steps.EnumerateArray().Select(s => (At: s.GetProperty("at").GetDateTimeOffset(), Step: s))
                    .OrderBy(s => s.At).ToArray();
                if (ordered.GroupBy(s => s.At).Any(g => g.Count() != 1)) throw new InvalidDataException("官网定价时间冲突");
                foreach (var step in ordered)
                    if (step.At <= now) effective = step.Step;
                    else if (step.At < expires) expires = step.At;
            }
            foreach (var (plan, account) in new[] { ("go", "individual-go-v1"), ("goat", "individual-goat"), ("pro", "individual-pro-v1") })
            {
                var entry = entries.SingleOrDefault(e => e.ModelId == id && e.PlanId == plan && !e.IsFree);
                if (entry is null || !Amount(effective, "planBudgetUsd", plan, out var budget) || budget <= 0) continue;
                var allowance = Amount(effective, "planAllowanceUsd", plan, out var a) ? a : budget;
                if (entry.MonthlyUsd != allowance) continue;
                var tier = Matches(tiers, "id:\"" + Regex.Escape(account)
                    + "\",[^{}]{1,1000}limits:\\{premiumCredits:0,opensourceCredits:(?<pool>[0-9.]+),totalCredits:(?<total>[0-9.]+),");
                if (tier.Count != 1 || !decimal.TryParse(tier[0].Groups["pool"].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var pool) || pool <= 0
                    || tier[0].Groups["pool"].Value != tier[0].Groups["total"].Value) continue;
                result.Add(new(id, plan, account, budget / pool, pool, expires,
                    "https://commandcode.ai/docs/resources/pricing-limits"));
            }
        }
        return result;
    }

    private static bool Amount(JsonElement model, string property, string plan, out decimal value)
    {
        value = 0;
        return model.TryGetProperty(property, out var amounts) && amounts.TryGetProperty(plan, out var amount)
            && amount.TryGetDecimal(out value) && value is >= 0 and <= 1_000_000;
    }
}
