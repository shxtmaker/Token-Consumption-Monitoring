using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services;

/// <summary>模型列表拉取（GET /models，按协议认证；向导"自动拉取"按钮用）。</summary>
public static class ModelFetcher
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<List<string>> FetchAsync(string baseUrl, KeyFormat.Protocol protocol, string key)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(baseUrl)) return result;

        var url = protocol == KeyFormat.Protocol.Anthropic
            ? $"{baseUrl.TrimEnd('/')}/v1/models"
            : $"{baseUrl.TrimEnd('/')}/models";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (protocol == KeyFormat.Protocol.Anthropic)
        {
            if (!string.IsNullOrEmpty(key)) request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return result;

        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        // OpenAI 兼容：{data:[{id}]}；Anthropic：{data:[{id}]}（同构）
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
                    result.Add(id.GetString()!);
            }
        }
        return result;
    }
}
