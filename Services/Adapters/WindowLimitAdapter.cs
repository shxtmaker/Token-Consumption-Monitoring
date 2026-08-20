using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services.Adapters;

/// <summary>
/// opencode 网关适配器：三窗口限额（/zen/go/v1/usage，Bearer key）+ 窗口绝对值（/api/go/status，OAuth 会话）。
/// </summary>
public sealed class WindowLimitAdapter : IPageAdapter
{
    private readonly OpenCodeUsageClient _client;
    private readonly OpenCodeAuthService _auth;

    public WindowLimitAdapter(OpenCodeUsageClient client, OpenCodeAuthService auth)
    {
        _client = client;
        _auth = auth;
    }

    public AdapterKind Kind => AdapterKind.WindowLimit;

    public async Task<PageData> FetchAsync(Page page, CancellationToken ct)
    {
        var key = CredentialStore.TryReadSecret(page.KeyTarget, out var k) ? k : null;
        if (string.IsNullOrEmpty(key))
            return new PageData { Status = ConnectionStatus.AuthError, StatusLabel = "未配置 key", Error = "请在页面编辑中填写 API key" };

        try
        {
            // Base URL 可能带 API 路径（如 /zen/go/v1）——usage/go-status 端点固定挂在服务器根
            var server = OpenCodeUsageClient.DeriveServer(page.BaseUrl);
            // 瞬时网络抖动（SSL 握手失败等）：重试一次
            OpenCodeUsageClient.WindowUsage wuR, wuW, wuM;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    (wuR, wuW, wuM) = await _client.FetchWindowUsageAsync(server, key, ct);
                    break;
                }
                catch (HttpRequestException) when (attempt == 0)
                {
                    Logger.Log("opencode window usage: 首次失败，2s 后重试");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }

            // 绝对值（OAuth 会话可用时）
            (long? L, long? R)? rollingAbs = null, weeklyAbs = null, monthlyAbs = null;
            var tokens = await _auth.EnsureFreshAsync(ct);
            if (tokens is not null)
            {
                var meters = await _client.FetchGoStatusAsync(server, tokens.AccessToken, tokens.OrgId, ct);
                foreach (var m in meters)
                {
                    var pair = (m.LimitMicroCents, m.RemainingMicroCents);
                    switch (m.Kind)
                    {
                        case "five_hour": rollingAbs = pair; break;
                        case "calendar_week": weeklyAbs = pair; break;
                        case "product_period": monthlyAbs = pair; break;
                    }
                }
            }

            return new PageData
            {
                Status = ConnectionStatus.Ok,
                StatusLabel = "正常",
                Rolling = (wuR.Percent, wuR.Status, wuR.ResetsAt),
                Weekly = (wuW.Percent, wuW.Status, wuW.ResetsAt),
                Monthly = (wuM.Percent, wuM.Status, wuM.ResetsAt),
                RollingAbsolute = rollingAbs,
                WeeklyAbsolute = weeklyAbs,
                MonthlyAbsolute = monthlyAbs,
            };
        }
        catch (InvalidOperationException ex)
        {
            return new PageData { Status = ConnectionStatus.Offline, StatusLabel = "获取失败", Error = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Logger.LogException("opencode window fetch", ex);
            return new PageData { Status = ConnectionStatus.Offline, StatusLabel = "连接中断", Error = "超时/网络错误" };
        }
    }

    public Task<(bool Ok, string Error)> ProbeAsync(Page page, CancellationToken ct)
    {
        var key = CredentialStore.TryReadSecret(page.KeyTarget, out var k) ? k : null;
        if (string.IsNullOrEmpty(key)) return Task.FromResult((false, "未配置 key"));
        return ProbeCoreAsync(page.BaseUrl, key, ct);
    }

    private async Task<(bool Ok, string Error)> ProbeCoreAsync(string baseUrl, string key, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{OpenCodeUsageClient.DeriveServer(baseUrl)}/zen/go/v1/models");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var response = await new HttpClient().SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode) return (true, "");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (false, "401 密钥无效");
            return (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (false, "超时/网络错误");
        }
    }
}
