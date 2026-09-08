using System.Globalization;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>OpenAI/Anthropic 组织级最近已完成 UTC 日用量或费用。分页失败时不发布部分总数。</summary>
public sealed class OrganizationUsageMethod : OfficialJsonQueryMethod
{
    private readonly bool _cost;
    private readonly bool _anthropic;
    private readonly Func<DateTimeOffset> _now;

    public OrganizationUsageMethod(bool anthropic, bool cost, HttpClient? http = null,
        Func<PageConfigRecord, CredentialClass, string?>? readSecret = null, Func<DateTimeOffset>? now = null)
        : base($"{(anthropic ? "anthropic" : "openai")}.organization-{(cost ? "cost" : "usage")}.admin-key",
            anthropic ? "anthropic" : "openai", CredentialClass.AdminKey,
            cost ? CapabilityKind.ReportedCost : CapabilityKind.ReportedUsage,
            [anthropic ? "api.anthropic.com" : "api.openai.com"], ["", "/v1"], http, readSecret)
    {
        _cost = cost;
        _anthropic = anthropic;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        var now = _now().ToUniversalTime();
        var end = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var start = end.AddDays(-1);
        var path = _anthropic
            ? $"/v1/organizations/{(_cost ? "cost_report" : "usage_report/messages")}?starting_at={Uri.EscapeDataString(start.ToString("O"))}&ending_at={Uri.EscapeDataString(end.ToString("O"))}&bucket_width=1d&limit=1"
            : $"/v1/organization/{(_cost ? "costs" : "usage/completions")}?start_time={start.ToUnixTimeSeconds()}&end_time={end.ToUnixTimeSeconds()}&bucket_width=1d&limit=1";
        if (!_cost) path += "&group_by%5B%5D=model";
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        var costs = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, (long Tokens, long Input, long Output, long Cache)>(StringComparer.Ordinal);
        long total = 0;
        long? requests = _anthropic ? null : 0;
        var hadRows = false;
        string? next = null;
        for (var count = 0; ; count++)
        {
            if (count >= 100) throw Schema("组织统计分页超过安全上限，未发布部分结果");
            using var doc = await GetAsync(page, path + (next is null ? "" : "&page=" + Uri.EscapeDataString(next)), key, ct);
            var root = doc.RootElement;
            foreach (var bucket in root.GetProperty("data").EnumerateArray())
            {
                var bucketStart = _anthropic
                    ? DateTimeOffset.Parse(Text(bucket, "starting_at"), CultureInfo.InvariantCulture)
                    : DateTimeOffset.FromUnixTimeSeconds(Count(bucket, "start_time"));
                var bucketEnd = _anthropic
                    ? DateTimeOffset.Parse(Text(bucket, "ending_at"), CultureInfo.InvariantCulture)
                    : DateTimeOffset.FromUnixTimeSeconds(Count(bucket, "end_time"));
                if (bucketStart < start || bucketEnd > end || bucketEnd <= bucketStart)
                    throw Schema("组织统计返回了请求范围以外的时间桶");
                foreach (var row in bucket.GetProperty("results").EnumerateArray())
                {
                    hadRows = true;
                    if (_cost)
                    {
                        var amount = _anthropic ? row : row.GetProperty("amount");
                        var currency = Text(amount, "currency").ToUpperInvariant();
                        var value = Number(amount, _anthropic ? "amount" : "value") / (_anthropic ? 100m : 1m);
                        costs[currency] = checked(costs.GetValueOrDefault(currency) + value);
                    }
                    else
                    {
                        var output = Count(row, "output_tokens");
                        long input, cached;
                        if (_anthropic)
                        {
                            cached = Count(row, "cache_read_input_tokens");
                            var creation = row.GetProperty("cache_creation");
                            input = checked(Count(row, "uncached_input_tokens") + cached
                                + Count(creation, "ephemeral_1h_input_tokens") + Count(creation, "ephemeral_5m_input_tokens"));
                        }
                        else
                        {
                            input = Count(row, "input_tokens");
                            cached = row.TryGetProperty("input_cached_tokens", out _) ? Count(row, "input_cached_tokens") : 0;
                            requests = checked(requests + Count(row, "num_model_requests"));
                        }
                        var tokens = checked(input + output);
                        total = checked(total + tokens);
                        var model = row.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "未分模型";
                        var previous = models.GetValueOrDefault(model);
                        models[model] = (checked(previous.Tokens + tokens), checked(previous.Input + input),
                            checked(previous.Output + output), checked(previous.Cache + cached));
                    }
                }
            }
            if (!root.GetProperty("has_more").GetBoolean()) break;
            next = Text(root, "next_page");
            if (!seenPages.Add(next)) throw Schema("组织统计分页游标重复，未发布部分结果");
        }
        if (!hadRows) return [];
        var coverage = new Coverage(start, end, Granularity.PerDay,
            _cost ? "组织费用 · UTC 昨日（账单可能延迟）"
                : _anthropic ? "组织 Messages 用量 · UTC 昨日" : "组织模型生成用量 · UTC 昨日");
        var expiry = now.AddMinutes(5);
        if (_cost) return costs.Select(x => (CapabilityValue)new ReportedCostValue(CapabilityKind.ReportedCost,
            Identity(page), Scope(page), coverage, now, 1, false, false, x.Value, x.Key, expiry)).ToArray();
        return [new ReportedUsageValue(CapabilityKind.ReportedUsage, Identity(page), Scope(page), coverage,
            now, 1, false, false, total, requests,
            models.Select(x => new ModelUsageRow(x.Key, x.Value.Tokens, Breakdown:
                new TokenBreakdown(x.Value.Input, x.Value.Output, x.Value.Cache, x.Value.Input - x.Value.Cache))).ToArray(), expiry)];
    }
}
