namespace TokenConsumptionMonitoring.Services;

/// <summary>opencode OAuth 会话：设备码登录、凭据恢复、过期刷新与 token 供给。</summary>
public sealed class OpenCodeAuthService
{
    private readonly OAuthDeviceFlowClient _oauth;
    private OAuthTokens? _tokens;

    public OpenCodeAuthService(OAuthDeviceFlowClient oauth) => _oauth = oauth;

    public bool IsLoggedIn => _tokens is not null;

    /// <summary>启动时从凭据管理器恢复 OAuth 会话。</summary>
    public bool TryLoadSession()
    {
        if (CredentialStore.TryReadSecret(CredentialStore.OAuthTarget, out var json) && !string.IsNullOrEmpty(json))
            try
            {
                var t = System.Text.Json.JsonSerializer.Deserialize<OAuthTokens>(json);
                if (t is { AccessToken.Length: > 0, RefreshToken.Length: > 0 }) { _tokens = t; return true; }
            }
            catch { }
        return false;
    }

    /// <summary>设备码登录。返回给用户的提示文案。</summary>
    public async Task<string> LoginAsync(CancellationToken ct)
    {
        var server = OAuthDeviceFlowClient.DefaultAuthServer;
        var flow = await _oauth.BeginAsync(server, ct);
        OpenVerificationUrl(flow);
        var tokens = await _oauth.PollAsync(flow, ct);
        var (_, orgId, _) = await _oauth.FetchAccountAsync(server, tokens.AccessToken, ct);
        if (orgId is not null)
            tokens = new OAuthTokens
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresIn = tokens.ExpiresIn,
                IssuedAt = tokens.IssuedAt,
                OrgId = orgId,
            };
        _tokens = tokens;
        CredentialStore.SaveSecret(CredentialStore.OAuthTarget, System.Text.Json.JsonSerializer.Serialize(tokens));
        return "OpenCode 登录成功";
    }

    /// <summary>返回有效 token；快过期时刷新。未登录返回 null。</summary>
    public async Task<OAuthTokens?> EnsureFreshAsync(CancellationToken ct)
    {
        if (_tokens is null) return null;
        if (_tokens.IsExpiringSoon)
            try
            {
                _tokens = await _oauth.RefreshAsync(OAuthDeviceFlowClient.DefaultAuthServer, _tokens.RefreshToken, ct);
                CredentialStore.SaveSecret(CredentialStore.OAuthTarget, System.Text.Json.JsonSerializer.Serialize(_tokens));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Logger.LogException("opencode token refresh", ex); }
        return _tokens;
    }

    private static void OpenVerificationUrl(DeviceFlowSession flow)
    {
        var url = flow.VerificationUriComplete.StartsWith("http") ? flow.VerificationUriComplete
                                                                  : flow.Server + flow.VerificationUriComplete;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}
