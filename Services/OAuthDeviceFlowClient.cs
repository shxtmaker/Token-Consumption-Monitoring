using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenUsageMonitorV3.Services;

/// <summary>
/// OpenCode 控制台 OAuth 设备码流（v2 已端到端验证：form-encoded、x-org-id、30 天 token）。
/// </summary>
public sealed class OAuthDeviceFlowClient
{
    public const string DefaultAuthServer = "https://console.opencode.ai";
    private const string ClientId = "opencode-cli";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    public async Task<DeviceFlowSession> BeginAsync(string server, CancellationToken ct)
    {
        using var content = Form(new Dictionary<string, string> { ["client_id"] = ClientId });
        var resp = await _http.PostAsync($"{server.TrimEnd('/')}/auth/device/code", content, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct)
            ?? throw new InvalidOperationException("empty device code response");
        return new DeviceFlowSession
        {
            Server = server.TrimEnd('/'),
            DeviceCode = json["device_code"]?.GetValue<string>() ?? throw new InvalidOperationException("no device_code"),
            VerificationUriComplete = json["verification_uri_complete"]?.GetValue<string>() ?? "/device",
            ExpiresIn = json["expires_in"]?.GetValue<int>() ?? 600,
            Interval = json["interval"]?.GetValue<int>() ?? 5,
        };
    }

    public async Task<OAuthTokens> PollAsync(DeviceFlowSession flow, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(flow.ExpiresIn);
        var interval = flow.Interval;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, interval)), ct);

            using var content = Form(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = flow.DeviceCode,
                ["client_id"] = ClientId,
            });
            using var response = await _http.PostAsync($"{flow.Server}/auth/device/token", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                return new OAuthTokens
                {
                    AccessToken = root.GetProperty("access_token").GetString() ?? "",
                    RefreshToken = root.GetProperty("refresh_token").GetString() ?? "",
                    ExpiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600,
                };
            }

            var error = "";
            try { using var doc = JsonDocument.Parse(body); error = doc.RootElement.GetProperty("error").GetString() ?? ""; } catch { }
            switch (error)
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                case "expired_token": throw new InvalidOperationException("授权已过期，请重新登录");
                case "access_denied": throw new InvalidOperationException("你在浏览器中拒绝了授权");
                default: throw new InvalidOperationException($"设备码轮询失败: {(string.IsNullOrEmpty(error) ? $"HTTP {(int)response.StatusCode}" : error)}");
            }
        }
        throw new TimeoutException("设备码已过期，请重新登录");
    }

    public async Task<OAuthTokens> RefreshAsync(string server, string refreshToken, CancellationToken ct)
    {
        using var content = Form(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        });
        using var response = await _http.PostAsync($"{server.TrimEnd('/')}/auth/device/token", content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidOperationException("empty refresh response");
        return new OAuthTokens
        {
            AccessToken = json["access_token"]?.GetValue<string>() ?? "",
            RefreshToken = json["refresh_token"]?.GetValue<string>() ?? "",
            ExpiresIn = json["expires_in"]?.GetValue<int>() ?? 3600,
        };
    }

    /// <summary>取账户信息与 org id（usage 接口需要 x-org-id）。</summary>
    public async Task<(string? Email, string? OrgId, string? OrgName)> FetchAccountAsync(string server, string accessToken, CancellationToken ct)
    {
        using var userReq = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}/api/user");
        userReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var userResp = await _http.SendAsync(userReq, ct);
        var email = userResp.IsSuccessStatusCode
            ? (await userResp.Content.ReadFromJsonAsync<JsonObject>(ct))?["email"]?.GetValue<string>() : null;

        using var orgsReq = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}/api/orgs");
        orgsReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var orgsResp = await _http.SendAsync(orgsReq, ct);
        string? orgId = null;
        string? orgName = null;
        if (orgsResp.IsSuccessStatusCode)
        {
            var orgs = await orgsResp.Content.ReadFromJsonAsync<JsonArray>(ct);
            if (orgs is { Count: > 0 } && orgs[0] is JsonObject first)
            {
                orgId = first["id"]?.GetValue<string>();
                orgName = first["name"]?.GetValue<string>();
            }
        }
        return (email, orgId, orgName);
    }
}

public sealed class DeviceFlowSession
{
    public required string Server { get; init; }
    public required string DeviceCode { get; init; }
    public required string VerificationUriComplete { get; init; }
    public int ExpiresIn { get; init; } = 600;
    public int Interval { get; init; } = 5;
}

public sealed class OAuthTokens
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public int ExpiresIn { get; init; } = 3600;
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? OrgId { get; init; }
    public DateTimeOffset ExpiresAt => IssuedAt.AddSeconds(ExpiresIn);
    public bool IsExpiringSoon => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-5);
}
