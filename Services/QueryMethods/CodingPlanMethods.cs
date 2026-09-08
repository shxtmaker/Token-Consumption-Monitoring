using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>读取智谱/Z.ai 官方用量插件公开的配额字段；不推断未公开的周窗口或重置时间。</summary>
public sealed class ZaiCodingPlanMethod(HttpClient? http = null,
    Func<PageConfigRecord, CredentialClass, string?>? readSecret = null)
    : OfficialJsonQueryMethod("zai.coding-plan.windows.api-key", "zai", CredentialClass.ApiKey, CapabilityKind.RollingWindow,
        ["api.z.ai", "open.bigmodel.cn"], ["", "/api/anthropic", "/api/paas/v4", "/api/coding/paas/v4"],
        http, readSecret, SourceStability.OfficialConditional)
{
    protected override void ApplyAuthentication(HttpRequestMessage request, string key)
    {
        // 与第一方插件一致；此来源的 Authorization 值不增加 Bearer 前缀。
        request.Headers.Add("Authorization", key);
        request.Headers.Add("Accept-Language", "en-US,en");
    }

    protected override async Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        using var doc = await GetAsync(page, "/api/monitor/usage/quota/limit", key, ct);
        var values = new List<CapabilityValue>();
        var types = new HashSet<string>();
        foreach (var row in doc.RootElement.GetProperty("data").GetProperty("limits").EnumerateArray())
        {
            var type = Text(row, "type");
            if (type is not ("TOKENS_LIMIT" or "TIME_LIMIT")) continue;
            if (!types.Add(type)) throw Schema("配额响应包含重复的窗口类型");
            var percent = Number(row, "percentage");
            if (percent is < 0 or > 100) throw Schema("配额百分比超出已支持范围");
            var now = DateTimeOffset.UtcNow;
            values.Add(new RollingWindowValue(CapabilityKind.RollingWindow, Identity(page), Scope(page), Coverage.Unknown,
                now, 0.9, false, false, type, type == "TOKENS_LIMIT" ? "Token 用量（5 小时）" : "MCP 用量（月）",
                "未提供重置时间", null, null, null, (int)Math.Round(percent), null, "%", now.AddMinutes(5)));
        }
        return values;
    }
}

/// <summary>MiniMax Token Plan 显式剩余百分比；旧计数存在歧义时不进行推算。</summary>
public sealed class MiniMaxTokenPlanMethod(HttpClient? http = null,
    Func<PageConfigRecord, CredentialClass, string?>? readSecret = null)
    : OfficialJsonQueryMethod("minimax.token-plan.windows.api-key", "minimax", CredentialClass.ApiKey, CapabilityKind.RollingWindow,
        ["www.minimaxi.com", "www.minimax.io"], ["", "/v1"], http, readSecret, SourceStability.OfficialConditional)
{
    protected override async Task<IReadOnlyList<CapabilityValue>> FetchAsync(PageConfigRecord page, string key, CancellationToken ct)
    {
        using var doc = await GetAsync(page, "/v1/token_plan/remains", key, ct);
        var root = doc.RootElement;
        if (Number(root.GetProperty("base_resp"), "status_code") != 0)
            throw Schema("MiniMax 套餐查询业务响应未成功");
        var values = new List<CapabilityValue>();
        var seen = new HashSet<string>();
        var ambiguous = false;
        foreach (var row in root.GetProperty("model_remains").EnumerateArray())
        {
            var model = Text(row, "model_name");
            if (!seen.Add(model)) throw Schema("MiniMax 返回重复的模型配额");
            if (OptionalNumber(row, "current_interval_status") == 3 && OptionalNumber(row, "current_weekly_status") == 3
                && OptionalNumber(row, "current_interval_total_count") == 0 && OptionalNumber(row, "current_weekly_total_count") == 0)
                continue;
            AddWindow(row, model, false);
            AddWindow(row, model, true);
        }
        if (values.Count == 0 && ambiguous) throw Schema("MiniMax 未返回可确认的显式剩余百分比，未使用旧计数推算");
        return values;

        void AddWindow(JsonElement row, string model, bool weekly)
        {
            var prefix = weekly ? "current_weekly" : "current_interval";
            if (OptionalNumber(row, prefix + "_status") == 3) return;
            var remaining = OptionalNumber(row, prefix + "_remaining_percent");
            if (remaining is null) { ambiguous = true; return; }
            if (remaining is < 0 or > 100) throw Schema("MiniMax 原始剩余百分比超出已支持范围");
            var start = DateTimeOffset.FromUnixTimeMilliseconds(Count(row, weekly ? "weekly_start_time" : "start_time"));
            var end = DateTimeOffset.FromUnixTimeMilliseconds(Count(row, weekly ? "weekly_end_time" : "end_time"));
            if (end <= start) throw Schema("MiniMax 返回无效的配额窗口");
            var now = DateTimeOffset.UtcNow;
            var boost = weekly ? OptionalNumber(row, "weekly_boost_permille") : null;
            var status = boost is > 0 && boost != 1000
                ? $"按当前分配额度显示；周加成 {boost.Value / 1000m:0.###}×"
                : "按当前分配额度显示";
            values.Add(new RollingWindowValue(CapabilityKind.RollingWindow, Identity(page), Scope(page),
                new Coverage(start, end, Granularity.PerWindow, model), now, 0.9, false, false,
                $"{model}:{(weekly ? "week" : "interval")}", $"{model} · {(weekly ? "周窗口" : "当前窗口")}",
                status, null, null, null, (int)Math.Round(100 - remaining.Value), end, "%",
                end < now.AddMinutes(5) ? end : now.AddMinutes(5)));
        }
    }
}
