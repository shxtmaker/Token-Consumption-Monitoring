using System.Text.Json;

namespace TokenUsageMonitorV3.Services;

/// <summary>
/// DeepSeek 控制台用量解析（经 WebView2 会话 fetch，schema 来自前端 bundle 实测：
/// GET /api/v0/usage/by_api_key/amount → series[].buckets[].usage{PROMPT_CACHE_HIT_TOKEN,...}
/// GET /api/v0/usage/by_api_key/cost → data[].series[].buckets[].cost）
/// </summary>
public sealed class DeepSeekUsageClient
{
    private readonly DeepSeekSessionService _session;

    public DeepSeekUsageClient(DeepSeekSessionService session) => _session = session;

    public sealed record UsageResult(
        long CacheHitTokens, long CacheMissTokens, long ResponseTokens, long RequestCount, decimal CostCny);

    /// <summary>拉取 [startMs, endMs]（unix 毫秒）范围内按模型的用量与金额。tz 为本地时区整小时秒偏移。</summary>
    public async Task<Dictionary<string, UsageResult>> FetchTodayByModelAsync(long startMs, long endMs, int tzSec)
    {
        var result = new Dictionary<string, UsageResult>(StringComparer.OrdinalIgnoreCase);
        var startSec = startMs / 1000;
        var endSec = endMs / 1000;

        // 优先页面自身请求捕获（风控上下文完整）；失败回退注入 fetch
        string? amountBody = null;
        var (okPage, bodyPage) = await _session.FetchUsageViaPageAsync(
            "https://platform.deepseek.com/usage",
            "/api/v0/usage/by_api_key/amount");
        if (okPage)
        {
            Logger.Log($"deepseek amount via page: {(bodyPage.Length > 400 ? bodyPage[..400] : bodyPage)}");
            amountBody = bodyPage;
        }
        else
        {
            var (okA, bodyA, invalidA) = await _session.FetchAsync(
                $"/api/v0/usage/by_api_key/amount?start={startSec}&end={endSec}&tz={tzSec}");
            if (invalidA) throw new InvalidOperationException("DeepSeek 会话失效");
            if (okA && bodyA != "{}") amountBody = bodyA;
        }
        if (amountBody is null) return result;

        var costs = await FetchCostAsync(startSec, endSec, tzSec);

        using var doc = JsonDocument.Parse(amountBody);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("biz_data", out var biz)) return result;
        if (!biz.TryGetProperty("series", out var seriesArrA)) return result;

        foreach (var series in seriesArrA.EnumerateArray())
        {
            var model = series.TryGetProperty("model", out var m) ? m.GetString() ?? "其他" : "其他";
            long hit = 0, miss = 0, resp = 0, req = 0;
            if (!series.TryGetProperty("buckets", out var bucketsA)) continue;
            foreach (var bucket in bucketsA.EnumerateArray())
            {
                // 页面默认时间范围可能大于今日——按桶时间过滤（bucket.time 为秒）
                if (bucket.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number
                    && t.TryGetInt64(out var bucketSec) && (bucketSec < startSec || bucketSec >= endSec + 86400))
                    continue;
                if (!bucket.TryGetProperty("usage", out var usage)) continue;
                hit += GetLong(usage, "PROMPT_CACHE_HIT_TOKEN");
                miss += GetLong(usage, "PROMPT_CACHE_MISS_TOKEN");
                resp += GetLong(usage, "RESPONSE_TOKEN");
                req += GetLong(usage, "REQUEST");
            }
            var cost = costs.TryGetValue(model, out var c) ? c : 0m;
            result[model] = new UsageResult(hit, miss, resp, req, cost);
        }
        return result;
    }

    private async Task<Dictionary<string, decimal>> FetchCostAsync(long startSec, long endSec, int tzSec)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        // 页面捕获优先（注入 fetch 被风控返回空）
        string? costBody = null;
        var (okPage, bodyPage) = await _session.FetchUsageViaPageAsync(
            "https://platform.deepseek.com/usage",
            "/api/v0/usage/by_api_key/cost");
        if (okPage)
        {
            Logger.Log($"deepseek cost via page: {(bodyPage.Length > 400 ? bodyPage[..400] : bodyPage)}");
            costBody = bodyPage;
        }
        else
        {
            var (ok, body, _) = await _session.FetchAsync(
                $"/api/v0/usage/by_api_key/cost?start={startSec}&end={endSec}&tz={tzSec}");
            if (ok && body != "{}") costBody = body;
        }
        if (costBody is null) return result;

        using var doc = JsonDocument.Parse(costBody);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("biz_data", out var biz)) return result;

        if (biz.TryGetProperty("data", out var seriesArr))
        {
            foreach (var entry in seriesArr.EnumerateArray())
            {
                if (!entry.TryGetProperty("series", out var seriesArrC)) continue;
                foreach (var series in seriesArrC.EnumerateArray())
                {
                    var model = series.TryGetProperty("model", out var m) ? m.GetString() ?? "其他" : "其他";
                    decimal sum = 0;
                    if (!series.TryGetProperty("buckets", out var bucketsC)) continue;
                    foreach (var bucket in bucketsC.EnumerateArray())
                    {
                        // 过滤今日桶（页面默认 30 天范围）
                        if (bucket.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number
                            && t.TryGetInt64(out var bucketSec) && (bucketSec < startSec || bucketSec >= endSec + 86400))
                            continue;
                        sum += GetDecimal(bucket, "cost");
                    }
                    result[model] = sum;
                }
            }
        }
        return result;
    }

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static decimal GetDecimal(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var d2)) return d2;
        return 0;
    }
}
