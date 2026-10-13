using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Services.QueryMethods;

namespace TokenConsumptionMonitoring.Services.QuotaReferences;

public interface IOfficialQuotaCatalogService
{
    Task<ModelQuotaCatalog> GetAsync(string provider, bool force, CancellationToken ct);
}

/// <summary>公开官网参考目录：与页面凭据及真实账户快照隔离。缓存仅保存公开数据。</summary>
public sealed class OfficialQuotaCatalogService : IOfficialQuotaCatalogService, IDisposable
{
    public const string OpenDesignPricingUrl = "https://open-design.ai/pricing/";
    public static string CommandCodePlanUrl(string plan) => "https://commandcode.ai/docs/plans/" + plan;
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ModelQuotaCatalog> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);

    public OfficialQuotaCatalogService(string? directory = null, HttpClient? http = null)
    {
        _directory = directory ?? Path.Combine(SettingsStore.DataDirectory, "public-quota-reference");
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    }

    public static string? ProviderOf(string url) => OpenDesignConsoleMethod.MatchesApiEndpoint(url)
        || OpenDesignConsoleMethod.TryGetWorkspace(url, out _) ? "opendesign"
        : Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.IsDefaultPort
          && u.UserInfo.Length == 0 && (u.IdnHost.Equals("api.commandcode.ai", StringComparison.OrdinalIgnoreCase)
            || u.IdnHost.Equals("commandcode.ai", StringComparison.OrdinalIgnoreCase)) ? "commandcode" : null;

    public async Task<ModelQuotaCatalog> GetAsync(string provider, bool force, CancellationToken ct)
    {
        if (provider is not ("opendesign" or "commandcode")) throw new ArgumentException("不支持此供应商", nameof(provider));
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!_cache.TryGetValue(provider, out var cached)) cached = ReadCache(provider, now);
            if (!force && _retryAfter.TryGetValue(provider, out var retry) && retry > now)
                return cached is not null ? cached with { IsStale = true, Warning = "官网同步失败，显示上次参考" }
                    : throw new HttpRequestException("官网暂时无法同步，请稍后重试");
            if (!force && cached is not null && cached.ConversionRules is not null
                && (cached.ConversionRules.Count > 0 || now - cached.FetchedAt < TimeSpan.FromMinutes(5))
                && !cached.ConversionRules.Any(r => r.ValidUntil <= now)
                && now - cached.FetchedAt < TimeSpan.FromHours(24)) return cached;
            try
            {
                var entries = new List<ModelQuotaReference>();
                var bodies = new List<string>();
                if (provider == "opendesign")
                {
                    var html = await ReadAsync(OpenDesignPricingUrl, ct);
                    bodies.Add(html);
                    entries.AddRange(OfficialQuotaParser.ParseOpenDesign(html));
                }
                else
                {
                    // 所有套餐均完成校验后才替换快照，避免把部分失败当作模型不受支持。
                    foreach (var plan in new[] { "go", "goat", "pro", "max" })
                    {
                        var html = await ReadAsync(CommandCodePlanUrl(plan), ct);
                        bodies.Add(html);
                        entries.AddRange(OfficialQuotaParser.ParseCommandCode(html, plan));
                    }
                }
                IReadOnlyList<ModelQuotaConversionRule> rules = Array.Empty<ModelQuotaConversionRule>();
                string? warning = null;
                try
                {
                    if (provider == "opendesign")
                    {
                        var shell = await ReadAsync("https://open-design.ai/cloud/dashboard", ct);
                        var index = await ReadAsync("https://open-design.ai" + QuotaConversionParser.Asset(shell,
                            @"/cloud/assets/index-[a-zA-Z0-9_-]+\.js"), ct);
                        var script = await ReadAsync("https://open-design.ai/cloud/" + QuotaConversionParser.Asset(index,
                            @"assets/pricing-refund-policy-[a-zA-Z0-9_-]+\.js"), ct);
                        bodies.Add(script);
                        rules = QuotaConversionParser.OpenDesign(script, entries, now);
                    }
                    else
                    {
                        var pricing = await ReadAsync("https://commandcode.ai/docs/resources/pricing-limits", ct);
                        var billing = await ReadAsync("https://commandcode.ai/signin", ct);
                        var tiers = await ReadAsync("https://commandcode.ai" + QuotaConversionParser.Asset(billing,
                            @"/assets/plan-tiers-[a-zA-Z0-9_-]+\.js"), ct);
                        bodies.Add(pricing); bodies.Add(tiers);
                        rules = QuotaConversionParser.CommandCode(pricing, tiers, entries, now);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException
                    or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException
                    or OperationCanceledException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                { warning = "剩余额度换算规则暂不可用"; }
                var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", bodies))))[..16];
                var result = new ModelQuotaCatalog(provider, DateTimeOffset.UtcNow, version, entries, Warning: warning, ConversionRules: rules);
                _cache[provider] = result;
                _retryAfter.Remove(provider);
                WriteCache(provider, result);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or IOException
                or OperationCanceledException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                _retryAfter[provider] = now.AddMinutes(5);
                if (cached is not null) return cached with { IsStale = true, Warning = "官网同步失败，显示上次参考" };
                throw new InvalidDataException("官网参考额度暂不可用，请稍后同步", ex);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<string> ReadAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4_000_000) throw new InvalidDataException("官网响应过大");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[16_384];
        int read;
        while ((read = await stream.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + read > 4_000_000) throw new InvalidDataException("官网响应过大");
            buffer.Write(block, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private ModelQuotaCatalog? ReadCache(string provider, DateTimeOffset now)
    {
        try
        {
            var path = Path.Combine(_directory, provider + "-v1.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 2_000_000) return null;
            var data = JsonSerializer.Deserialize<ModelQuotaCatalog>(File.ReadAllText(path));
            if (data is null || data.Provider != provider || data.FetchedAt > now || string.IsNullOrEmpty(data.Version)
                || data.Entries is not { Count: > 0 and <= 3000 }
                || data.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.ModelName) || e.Note is null
                    || e.ModelId != OfficialQuotaParser.ModelId(provider, e.ModelName)
                    || e.MonthlyUsd is < 0 or > 1_000_000 || e.ModelName.Length > 160
                    || e.IsFree == (e.MonthlyUsd is not null)
                    || string.IsNullOrWhiteSpace(e.PlanName) || e.PlanId is not ("go" or "goat" or "plus" or "pro" or "max" or "ultra")
                    || e.SourceUrl != (provider == "opendesign" ? OpenDesignPricingUrl : CommandCodePlanUrl(e.PlanId == "ultra" ? "max" : e.PlanId)))) return null;
            if (data.Entries.GroupBy(e => (e.ModelId, e.PlanId)).Any(g => g.Count() != 1)) return null;
            if (data.ConversionRules is { } rules && (rules.Count > 3000
                || rules.Any(r => r is null || r.UsdPerPoolUnit is <= 0 or > 1000
                    || r.StandardMonthlyPool is <= 0 or > 1_000_000
                    || r.ValidUntil <= data.FetchedAt || r.ValidUntil > data.FetchedAt.AddHours(24)
                    || !data.Entries.Any(e => e.ModelId == r.ModelId && e.PlanId == r.PlanId && !e.IsFree)
                    || r.SourceUrl != (provider == "opendesign" ? "https://open-design.ai/model-credits/" : "https://commandcode.ai/docs/resources/pricing-limits"))
                || rules.GroupBy(r => (r.ModelId, r.PlanId)).Any(g => g.Count() != 1))) return null;
            _cache[provider] = data;
            return data;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    private void WriteCache(string provider, ModelQuotaCatalog data)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, provider + "-v1.json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Log("官网参考目录缓存未能保存"); }
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
