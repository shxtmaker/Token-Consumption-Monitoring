using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services.Adapters;

/// <summary>
/// 通用探测适配器：连接状态 + 模型列表（无官方用量数据源——OpenAI/Anthropic 普通 key 已核查无用量端点）。
/// 认证按协议：ChatCompletions/Responses → Bearer；Anthropic → x-api-key + anthropic-version。
/// </summary>
public sealed class ProbeAdapter : IPageAdapter
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public AdapterKind Kind => AdapterKind.Probe;

    public async Task<PageData> FetchAsync(Page page, CancellationToken ct)
    {
        var (ok, error) = await ProbeAsync(page, ct);
        if (!ok)
            return new PageData { Status = ConnectionStatus.Offline, StatusLabel = "连接失败", Error = error };

        var models = await FetchModelsAsync(page, ct);
        return new PageData
        {
            Status = ConnectionStatus.Ok,
            StatusLabel = "正常",
            Models = models,
        };
    }

    public async Task<(bool Ok, string Error)> ProbeAsync(Page page, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var (url, headers) = BuildRequest(page, "/models");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, page, headers);
            using var response = await _http.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode) return (true, "");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (false, "401 密钥无效");
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return (false, "403 无权限");
            return (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (false, "超时/网络错误");
        }
    }

    private async Task<List<string>> FetchModelsAsync(Page page, CancellationToken ct)
    {
        var result = new List<string>();
        try
        {
            var (url, headers) = BuildRequest(page, "/models");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, page, headers);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return result;

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var m in data.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
                        result.Add(id.GetString()!);
                }
            }
        }
        catch { }
        return result;
    }

    private static (string Url, string? AnthropicVersion) BuildRequest(Page page, string path)
    {
        var baseUrl = page.BaseUrl.TrimEnd('/');
        // Anthropic 协议：/v1/models
        var url = page.Protocol == KeyFormat.Protocol.Anthropic ? $"{baseUrl}/v1{path}" : $"{baseUrl}{path}";
        return (url, page.Protocol == KeyFormat.Protocol.Anthropic ? "2023-06-01" : null);
    }

    private void ApplyAuth(HttpRequestMessage request, Page page, string? anthropicVersion)
    {
        var key = CredentialStore.TryReadSecret(page.KeyTarget, out var k) ? k : null;
        if (page.Protocol == KeyFormat.Protocol.Anthropic)
        {
            if (!string.IsNullOrEmpty(key)) request.Headers.Add("x-api-key", key);
            if (!string.IsNullOrEmpty(anthropicVersion)) request.Headers.Add("anthropic-version", anthropicVersion);
        }
        else
        {
            if (!string.IsNullOrEmpty(key))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
    }
}
