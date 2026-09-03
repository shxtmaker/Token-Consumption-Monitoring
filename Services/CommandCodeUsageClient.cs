using System.Globalization;
using System.Text.Json;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;

namespace TokenConsumptionMonitoring.Services;

/// <summary>Command Code 鉴权失败（HTTP 401 或业务码 UNAUTHORIZED）。</summary>
public sealed class CommandCodeAuthException : QueryTransportException
{
    public CommandCodeAuthException(string message) : base(CandidateStatus.AuthRequired, message, 401) { }
}

/// <summary>查询方法对 Command Code 控制面的依赖面（测试可替换）。</summary>
public interface ICommandCodeUsageClient
{
    Task<string?> FetchOrgIdAsync(string apiKey, CancellationToken ct);
    Task<CommandCodeUsageClient.AccountUsage?> FetchUsageAsync(string apiKey, CancellationToken ct);
}

/// <summary>
/// Command Code /alpha 控制面客户端。
/// 只消费服务端直接返回的 used、limit、remaining、reset 字段，
/// 不根据套餐名或硬编码额度推导账务状态。
/// </summary>
public sealed class CommandCodeUsageClient : ICommandCodeUsageClient
{
    public const string ApiBase = "https://api.commandcode.ai";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record WindowLimit(double? Used, double? Limit, double? Remaining, DateTimeOffset? ResetAt)
    {
        public int? Percent
        {
            get
            {
                if (Used is { } used && Limit is { } limit && limit > 0)
                    return (int)Math.Round(Math.Max(0, Math.Min(100, used / limit * 100)));
                if (Remaining is { } remaining && Limit is { } limit2 && limit2 > 0)
                    return (int)Math.Round(Math.Max(0, Math.Min(100, (limit2 - remaining) / limit2 * 100)));
                return null;
            }
        }

        public long? LimitMicroCents => Limit is { } limit ? Money.ToMicroCents(limit) : null;
        public long? RemainingMicroCents => Remaining is { } remaining ? Money.ToMicroCents(Math.Max(0, remaining)) : null;
        public long? UsedMicroCents => Used is { } used ? Money.ToMicroCents(Math.Max(0, used)) : null;
    }

    public sealed record WindowLimits(bool Limited, WindowLimit? FiveHour, WindowLimit? Weekly);

    public sealed record Credits(WindowLimits? Limits, double? MonthlyRemaining)
    {
        public long? MonthlyRemainingMicroCents => MonthlyRemaining is { } remaining ? Money.ToMicroCents(remaining) : null;
    }

    public sealed record SubscriptionData(string? Status, DateTimeOffset? CurrentPeriodStart, DateTimeOffset? CurrentPeriodEnd);

    public sealed record AccountUsage(string? OrgId, Credits? Credits, SubscriptionData? Subscription = null);

    /// <summary>本地 CLI 登录凭据，仅作为显式页面的本地凭据发现来源。</summary>
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

    /// <summary>读取账户及服务端直接返回的窗口额度。</summary>
    public async Task<AccountUsage?> FetchUsageAsync(string apiKey, CancellationToken ct)
    {
        var orgId = await FetchOrgIdAsync(apiKey, ct);
        var credits = await FetchCreditsAsync(apiKey, orgId, ct);
        var subscription = await FetchSubscriptionsAsync(apiKey, orgId, ct);
        return credits is null && subscription is null ? null : new AccountUsage(orgId, credits, subscription);
    }

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

    public async Task<Credits?> FetchCreditsAsync(string apiKey, string? orgId, CancellationToken ct)
        => ParseCreditsBody(await GetAsync(AppendOrg("/alpha/billing/credits", orgId), apiKey, ct));

    public async Task<SubscriptionData?> FetchSubscriptionsAsync(string apiKey, string? orgId, CancellationToken ct)
        => ParseSubscriptionBody(await GetAsync(AppendOrg("/alpha/billing/subscriptions", orgId), apiKey, ct));

    private static Credits? ParseCreditsBody(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object)
            return null;
        // monthlyCredits 是服务端直接返回的月度剩余（随用量递减），非月度总额；上限/已用无接口提供。
        return new Credits(ParseWindowLimits(root), Dbl(credits, "monthlyCredits"));
    }

    private static SubscriptionData? ParseSubscriptionBody(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;
        return new SubscriptionData(
            Str(data, "status"),
            Date(data, "currentPeriodStart"),
            Date(data, "currentPeriodEnd"));
    }

    private static WindowLimits? ParseWindowLimits(JsonElement root)
    {
        if (!root.TryGetProperty("windowLimits", out var limits) || limits.ValueKind != JsonValueKind.Object)
        {
            if (!root.TryGetProperty("credits", out var credits)
                || !credits.TryGetProperty("windowLimits", out limits)
                || limits.ValueKind != JsonValueKind.Object)
                return null;
        }
        return new WindowLimits(
            Bool(limits, "limited"),
            ParseWindow(limits, "fiveHour"),
            ParseWindow(limits, "weekly"));
    }

    private static WindowLimit? ParseWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;
        var used = Dbl(window, "used");
        var limit = Dbl(window, "limit") ?? Dbl(window, "cap");
        var remaining = Dbl(window, "remaining");
        if (used is null && limit is null && remaining is null) return null;
        return new WindowLimit(used, limit, remaining, Date(window, "reset" ) ?? Date(window, "resetAt"));
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
            var status = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => CandidateStatus.AuthRequired,
                System.Net.HttpStatusCode.Forbidden => CandidateStatus.Forbidden,
                System.Net.HttpStatusCode.TooManyRequests => CandidateStatus.RateLimited,
                _ when (int)response.StatusCode >= 500 => CandidateStatus.NetworkFailure,
                _ => CandidateStatus.SchemaMismatch,
            };
            if (status == CandidateStatus.AuthRequired)
                throw new CommandCodeAuthException("Command Code HTTP 401（key 无效）");
            throw new QueryTransportException(status, $"Command Code HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var code = doc.RootElement.TryGetProperty("error", out var error) ? Str(error, "code") : null;
            if (string.Equals(code, "UNAUTHORIZED", StringComparison.OrdinalIgnoreCase))
                throw new CommandCodeAuthException("Command Code UNAUTHORIZED（key 无效）");
            throw new QueryTransportException(CandidateStatus.SchemaMismatch, $"Command Code {code ?? "请求失败"}");
        }
        return body;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var number) ? number : null,
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var value)
           && (value.ValueKind == JsonValueKind.True
               || value.ValueKind == JsonValueKind.String
                  && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            return date;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var milliseconds))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); } catch { }
        }
        return null;
    }
}
