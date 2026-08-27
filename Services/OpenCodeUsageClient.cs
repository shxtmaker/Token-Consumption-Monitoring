using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;

namespace TokenConsumptionMonitoring.Services;

/// <summary>
/// OpenCode 官方客户端：
/// - 窗口限额：/zen/go/v1/usage（Bearer API key）+ /api/go/status（OAuth）
/// - 用量/金额：/api/usage/summary|models（OAuth + x-org-id，官方 token 字段）
/// </summary>
public sealed class OpenCodeUsageClient
{
    public const string DefaultServer = "https://opencode.ai";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>把用户填的 Base URL 规约为服务器地址（scheme://host），忽略路径（如 /zen/go/v1）。</summary>
    public static string DeriveServer(string baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
            return $"{uri.Scheme}://{uri.Host}";
        return DefaultServer;
    }

    // ---- 窗口限额（Bearer API key） ----

    public sealed record WindowUsage(string Status, int Percent, DateTimeOffset ResetsAt);

    public async Task<(WindowUsage Rolling, WindowUsage Weekly, WindowUsage Monthly)> FetchWindowUsageAsync(string server, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}/zen/go/v1/usage");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw TransportError(response.StatusCode, "usage");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var u = doc.RootElement.GetProperty("usage");
        return (Parse(u, "rolling"), Parse(u, "weekly"), Parse(u, "monthly"));
    }

    private static WindowUsage Parse(JsonElement usage, string name)
    {
        var w = usage.GetProperty(name);
        return new WindowUsage(
            w.GetProperty("status").GetString() ?? "",
            w.GetProperty("percent").GetInt32(),
            DateTimeOffset.Parse(w.GetProperty("resetsAt").GetString() ?? ""));
    }

    // ---- 窗口绝对值（OAuth + x-org-id） ----

    public sealed record GoMeter(string Kind, DateTimeOffset? ResetsAt,
        long? UsedMicroCents, long? LimitMicroCents, long? RemainingMicroCents);

    public async Task<List<GoMeter>> FetchGoStatusAsync(string server, string accessToken, string? orgId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}/api/go/status");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrEmpty(orgId)) request.Headers.Add("x-org-id", orgId);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw TransportError(response.StatusCode, "go/status");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var meters = new List<GoMeter>();
        if (!doc.RootElement.TryGetProperty("meters", out var arr)) return meters;
        foreach (var m in arr.EnumerateArray())
        {
            meters.Add(new GoMeter(
                GetString(m, "kind") ?? "",
                GetDateTime(m, "resetsAt"),
                GetLong(m, "usedMicroCents"),
                GetLong(m, "limitMicroCents"),
                GetLong(m, "remainingMicroCents")));
        }
        return meters;
    }

    // ---- 官方用量（OAuth + x-org-id） ----

    public sealed record UsageSummary(long TotalRequests, long TotalInputTokens, long TotalOutputTokens, long TotalCacheReadTokens, decimal TotalCostUsd);

    public sealed record ModelUsage(string Model, long InputTokens, long OutputTokens, long CacheReadTokens, decimal CostUsd);

    /// <summary>账户累计用量（summary）。时间语义：累计值，实现时核对。</summary>
    public async Task<UsageSummary?> FetchUsageSummaryAsync(string server, string accessToken, string? orgId, CancellationToken ct)
    {
        var body = await FetchSessionJsonAsync(server, "/api/usage/summary", accessToken, orgId, ct);
        if (body is null) return null;
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new UsageSummary(
            GetLong(r, "totalRequests") ?? 0,
            GetLong(r, "totalInputTokens") ?? 0,
            GetLong(r, "totalOutputTokens") ?? 0,
            GetLong(r, "totalCacheReadTokens") ?? 0,
            (GetLong(r, "totalCostMicroCents") ?? 0) / 10_000_000m);
    }

    /// <summary>按模型用量（models）。</summary>
    public async Task<List<ModelUsage>> FetchUsageModelsAsync(string server, string accessToken, string? orgId, CancellationToken ct)
    {
        var result = new List<ModelUsage>();
        var body = await FetchSessionJsonAsync(server, "/api/usage/models", accessToken, orgId, ct);
        if (body is null) return result;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return result;
        foreach (var m in data.EnumerateArray())
        {
            result.Add(new ModelUsage(
                GetString(m, "model") ?? "其他",
                GetLong(m, "totalInputTokens") ?? 0,
                GetLong(m, "totalOutputTokens") ?? 0,
                GetLong(m, "totalCacheReadTokens") ?? 0,
                (GetLong(m, "totalCostMicroCents") ?? 0) / 10_000_000m));
        }
        return result;
    }

    private async Task<string?> FetchSessionJsonAsync(string server, string path, string accessToken, string? orgId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}{path}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrEmpty(orgId)) request.Headers.Add("x-org-id", orgId);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw TransportError(response.StatusCode, path);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static QueryTransportException TransportError(System.Net.HttpStatusCode status, string endpoint)
    {
        var candidateStatus = status switch
        {
            System.Net.HttpStatusCode.Unauthorized => CandidateStatus.AuthRequired,
            System.Net.HttpStatusCode.Forbidden => CandidateStatus.Forbidden,
            System.Net.HttpStatusCode.TooManyRequests => CandidateStatus.RateLimited,
            _ when (int)status >= 500 => CandidateStatus.NetworkFailure,
            _ => CandidateStatus.SchemaMismatch,
        };
        return new QueryTransportException(candidateStatus,
            $"OpenCode {endpoint} HTTP {(int)status}", (int)status);
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var l2)) return l2;
        return null;
    }

    private static DateTimeOffset? GetDateTime(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;
}
