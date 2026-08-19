using System.Text.Json;

namespace TokenUsageMonitorV3.Services;

/// <summary>Command Code 鉴权失败（HTTP 401 或业务码 UNAUTHORIZED）：专用异常，调用方按类型捕获而非解析消息。</summary>
public sealed class CommandCodeAuthException : InvalidOperationException
{
    public CommandCodeAuthException(string message) : base(message) { }
}

/// <summary>
/// Command Code 官方客户端（/alpha 控制面，无公开文档，端点取自 CLI bundle command-code@1.28）：
/// - whoami：用户信息（org id）
/// - /alpha/billing/credits：套餐剩余额度 + 5h/周滚动窗口（Bearer API key，与 CLI 同一 key）
/// - /alpha/billing/subscriptions：订阅（套餐、状态、计费周期）
/// - /alpha/usage/summary：当前计费周期消费（totalCost，美元；必须带 since=周期起点，否则为全量历史）
/// 认证与 opencode 网关同类：Authorization: Bearer &lt;apiKey&gt;。
/// </summary>
public sealed class CommandCodeUsageClient
{
    public const string ApiBase = "https://api.commandcode.ai";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // ---- planId → 月额度（美元），取自 CLI bundle（getPlanTotalCredits） ----
    public static readonly IReadOnlyDictionary<string, int> PlanMonthlyCredits = new Dictionary<string, int>
    {
        ["individual-go"] = 10,
        ["individual-goat"] = 70,
        ["individual-pro"] = 30,
        ["individual-pro-v1"] = 80,
        ["individual-provider"] = 15,
        ["individual-max"] = 150,
        ["individual-ultra"] = 300,
        ["teams-pro"] = 40,
    };

    /// <summary>planId → 显示名。</summary>
    public static readonly IReadOnlyDictionary<string, string> PlanNames = new Dictionary<string, string>
    {
        ["individual-go"] = "Go",
        ["individual-goat"] = "GOAT",
        ["individual-pro"] = "Pro",
        ["individual-pro-v1"] = "Pro",
        ["individual-provider"] = "Provider",
        ["individual-max"] = "Max",
        ["individual-ultra"] = "Ultra",
        ["teams-pro"] = "Teams Pro",
    };

    internal static (string Name, int MonthlyCredits)? PlanOf(string? planId)
        => planId is not null && PlanMonthlyCredits.TryGetValue(planId, out var credits)
            ? (PlanNames.GetValueOrDefault(planId, planId), credits) : null;

    // ---- 数据模型（camelCase JSON，手动防御式解析） ----

    public sealed record WindowLimit(double Used, double Cap, DateTimeOffset? ResetAt)
    {
        public int Percent => Cap > 0 ? (int)Math.Round(Used / Cap * 100) : -1;
        public long LimitMicroCents => Money.ToMicroCents(Cap);
        public long RemainingMicroCents => Money.ToMicroCents(Math.Max(0, Cap - Used));
    }

    public sealed record WindowLimits(bool Limited, WindowLimit? FiveHour, WindowLimit? Weekly);

    public sealed record Credits(string? PlanId, double? MonthlyCredits, double? PurchasedCredits,
        double? FreeCredits, WindowLimits? Limits);

    public sealed record SubscriptionData(string? PlanId, string? Status,
        DateTimeOffset? CurrentPeriodStart, DateTimeOffset? CurrentPeriodEnd);

    /// <param name="TotalCost">当前计费周期消费（美元）；无订阅周期时为 null（不得用全量历史兜底）。</param>
    public sealed record AccountUsage(string? OrgId, Credits? Credits, SubscriptionData? Subscription, double? TotalCost)
    {
        public (string Name, int MonthlyCredits)? Plan => PlanOf(Credits?.PlanId ?? Subscription?.PlanId);
    }

    /// <summary>本地 CLI 登录凭据（%USERPROFILE%\.commandcode\auth.json）——尚未填写页面 key 时的回退。</summary>
    public static string? ReadLocalApiKey()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".commandcode", "auth.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("apiKey", out var key)
                && key.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(key.GetString()))
                return key.GetString();
        }
        catch (Exception ex) { Logger.LogException("commandcode auth.json read", ex); }
        return null;
    }

    /// <summary>拉取账户用量快照（whoami → org id → credits/subscription，summary 用订阅周期起点收敛）。</summary>
    public async Task<AccountUsage?> FetchUsageAsync(string apiKey, CancellationToken ct)
    {
        var orgId = await FetchOrgIdAsync(apiKey, ct);

        var creditsTask = FetchCreditsAsync(apiKey, orgId, ct);
        var subscriptionTask = FetchSubscriptionsAsync(apiKey, orgId, ct);
        var subscription = await subscriptionTask;

        // summary 必须带周期起点：无订阅周期时置 null，避免 totalCost 退化为全量历史消费
        var periodStart = subscription?.CurrentPeriodStart;
        double? summary = null;
        if (periodStart is { } ps)
            summary = await FetchSummaryAsync(apiKey, orgId, ps, ct);
        else
            Logger.Log("commandcode: 无订阅周期起点，跳过 usage/summary（月度窗口将不显示）");

        var credits = await creditsTask;

        return new AccountUsage(orgId, credits, subscription, summary);
    }

    /// <summary>GET /alpha/whoami → org.id（个人账户 org=null 时为 null，请求成功即可）。</summary>
    public async Task<string?> FetchOrgIdAsync(string apiKey, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync("/alpha/whoami", apiKey, ct));
        if (doc.RootElement.TryGetProperty("org", out var org)
            && org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        return null;
    }

    /// <summary>GET /alpha/billing/credits → 套餐剩余 + 窗口限额。</summary>
    public async Task<Credits?> FetchCreditsAsync(string apiKey, string? orgId, CancellationToken ct)
        => ParseCreditsBody(await GetAsync(AppendOrg("/alpha/billing/credits", orgId), apiKey, ct));

    /// <summary>GET /alpha/billing/subscriptions → 当前订阅（套餐/状态/计费周期）。</summary>
    public async Task<SubscriptionData?> FetchSubscriptionsAsync(string apiKey, string? orgId, CancellationToken ct)
        => ParseSubscriptionBody(await GetAsync(AppendOrg("/alpha/billing/subscriptions", orgId), apiKey, ct));

    /// <summary>GET /alpha/usage/summary?since=… → 当前计费周期消费（totalCost，美元）。since 必填（周期起点）。</summary>
    public async Task<double> FetchSummaryAsync(string apiKey, string? orgId, DateTimeOffset since, CancellationToken ct)
    {
        var path = AppendOrg("/alpha/usage/summary", orgId);
        path += (path.Contains('?') ? "&" : "?") + $"since={Uri.EscapeDataString(since.UtcDateTime.ToString("o"))}";
        return ParseSummaryBody(await GetAsync(path, apiKey, ct));
    }

    /// <summary>解析 credits 响应体（windowLimits 为 credits 的平级字段，planId 取自 subscriptions）。</summary>
    private static Credits? ParseCreditsBody(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("credits", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        return new Credits(
            Str(c, "planId"),
            Dbl(c, "monthlyCredits"),
            Dbl(c, "purchasedCredits"),
            Dbl(c, "freeCredits"),
            ParseWindowLimits(root));
    }

    /// <summary>解析 subscriptions 响应体。</summary>
    private static SubscriptionData? ParseSubscriptionBody(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        return new SubscriptionData(
            Str(d, "planId"),
            Str(d, "status"),
            Date(d, "currentPeriodStart"),
            Date(d, "currentPeriodEnd"));
    }

    /// <summary>解析 summary 响应体（totalCost，美元）。</summary>
    private static double ParseSummaryBody(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return Dbl(doc.RootElement, "totalCost") ?? 0;
    }

    private static string AppendOrg(string path, string? orgId)
        => string.IsNullOrEmpty(orgId) ? path : $"{path}?orgId={Uri.EscapeDataString(orgId)}";

    private async Task<string> GetAsync(string path, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new CommandCodeAuthException($"Command Code HTTP 401（key 无效）");
            throw new InvalidOperationException($"Command Code HTTP {(int)response.StatusCode}");
        }
        // 业务错误信封（code UNAUTHORIZED 等）
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var code = doc.RootElement.TryGetProperty("error", out var err) ? Str(err, "code") : null;
            if (string.Equals(code, "UNAUTHORIZED", StringComparison.OrdinalIgnoreCase))
                throw new CommandCodeAuthException("Command Code UNAUTHORIZED（key 无效）");
            throw new InvalidOperationException($"Command Code {code ?? "请求失败"}");
        }
        return body;
    }

    private static WindowLimits? ParseWindowLimits(JsonElement c)
    {
        if (!c.TryGetProperty("windowLimits", out var wl) || wl.ValueKind != JsonValueKind.Object) return null;
        return new WindowLimits(
            Bool(wl, "limited"),
            ParseWindow(wl, "fiveHour"),
            ParseWindow(wl, "weekly"));
    }

    private static WindowLimit? ParseWindow(JsonElement wl, string name)
    {
        if (!wl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        var used = Dbl(w, "used") ?? 0;
        var cap = Dbl(w, "cap");
        return cap is null ? null : new WindowLimit(used, cap.Value, Date(w, "resetAt"));
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), out var d) ? d : null,
            _ => null,
        };
    }

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v)
           && (v.ValueKind == JsonValueKind.True
               || v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d)) return d;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms))
        {
            // 毫秒时间戳（部分接口返回 epoch）
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); } catch { }
        }
        return null;
    }
}
