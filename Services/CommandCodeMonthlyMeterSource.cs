using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TokenConsumptionMonitoring.Services.QuotaReferences;

namespace TokenConsumptionMonitoring.Services;

public interface ICommandCodeMonthlyMeterSource
{
    Task<CommandCodeUsageClient.MonthlyMeter?> ReadAsync(CommandCodeUsageClient.Credits? credits,
        CommandCodeUsageClient.SubscriptionData? subscription, CancellationToken ct);
}

/// <summary>
/// 与官网 monthly-usage-meter 相同的月度限额口径。使用账户账务值及公开套餐总量，
/// 不使用模型价格或模型额度倍率；本对象只访问公开官网，绝不接收 API 密钥。
/// </summary>
public sealed class CommandCodeMonthlyMeterSource : ICommandCodeMonthlyMeterSource, IDisposable
{
    public sealed record Tier(string Id, bool IsOrganization, decimal TotalCredits);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, Tier>? _tiers;
    private DateTimeOffset _validUntil;
    private DateTimeOffset _retryAfter;

    public CommandCodeMonthlyMeterSource(HttpClient? http = null) => _http = http
        ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<CommandCodeUsageClient.MonthlyMeter?> ReadAsync(CommandCodeUsageClient.Credits? credits,
        CommandCodeUsageClient.SubscriptionData? subscription, CancellationToken ct)
    {
        if (credits?.MonthlyRemaining is null || string.IsNullOrEmpty(subscription?.PlanId)) return null;
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_tiers is null || now >= _validUntil)
            {
                if (now < _retryAfter) return null;
                var html = await ReadPublicAsync("https://commandcode.ai/signin", ct);
                var path = QuotaConversionParser.Asset(html, @"/assets/plan-tiers-[a-zA-Z0-9_-]+\.js");
                var script = await ReadPublicAsync("https://commandcode.ai" + path, ct);
                _tiers = ParseTiers(script);
                _validUntil = now.AddHours(24);
                _retryAfter = default;
            }
            return _tiers.TryGetValue(subscription.PlanId, out var tier) ? Normalize(credits, subscription, tier, now) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
            or OperationCanceledException or RegexMatchTimeoutException)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
            return null; // 公开规则故障不能抹除已经取得的账户窗口和 credits。
        }
        finally { _gate.Release(); }
    }

    public static IReadOnlyDictionary<string, Tier> ParseTiers(string script)
    {
        const string number = @"[0-9]+(?:\.[0-9]+)?(?:e[+-]?[0-9]+)?";
        var matches = Regex.Matches(script,
            "id:\"(?<id>[a-z0-9-]{1,80})\",name:\"[^\"]{1,100}\",type:\"(?<type>individual|org)\",[^{}]{1,1500}limits:\\{premiumCredits:(?<premium>"
            + number + "),opensourceCredits:(?<open>" + number + "),totalCredits:(?<total>" + number + "),",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        var tiers = new Dictionary<string, Tier>(StringComparer.Ordinal);
        foreach (Match m in matches)
        {
            if (!decimal.TryParse(m.Groups["total"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total)
                || !decimal.TryParse(m.Groups["premium"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var premium)
                || !decimal.TryParse(m.Groups["open"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var open)
                || total is <= 0 or > 1_000_000 || premium is < 0 or > 1_000_000 || open is < 0 or > 1_000_000
                || premium + open != total
                || !tiers.TryAdd(m.Groups["id"].Value, new(m.Groups["id"].Value, m.Groups["type"].Value == "org", total)))
                throw new InvalidDataException("Command Code 公开套餐限额无法确认");
        }
        if (tiers.Count == 0) throw new InvalidDataException("Command Code 公开套餐限额缺失");
        return tiers;
    }

    public static CommandCodeUsageClient.MonthlyMeter? Normalize(CommandCodeUsageClient.Credits credits,
        CommandCodeUsageClient.SubscriptionData subscription, Tier tier, DateTimeOffset now)
    {
        if (subscription.PlanId != tier.Id || subscription.Status is not ("active" or "trialing")
            || subscription.CurrentPeriodStart is null || subscription.CurrentPeriodStart > now
            || subscription.CurrentPeriodEnd is null || subscription.CurrentPeriodEnd <= now
            || credits.MonthlyRemaining is not { } remaining || !double.IsFinite(remaining)
            || Math.Abs(remaining) > 1e12 || tier.TotalCredits <= 0) return null;
        var total = tier.TotalCredits;
        // 官网：有效的授予量优先；否则团队套餐按有效席位数计算。
        if (credits.MonthlyGranted is { } grant && double.IsFinite(grant) && grant > 0 && grant <= 1e12)
            total = Math.Max(total, (decimal)grant);
        else if (tier.IsOrganization)
        {
            if (subscription.Quantity is not { } seats || !double.IsFinite(seats) || seats < 1 || seats > 10000) return null;
            total *= (decimal)Math.Floor(seats);
        }
        var used = Math.Clamp(total - Math.Max(0, (decimal)remaining), 0, total);
        var percent = (int)decimal.Round(used / total * 100, 0, MidpointRounding.AwayFromZero);
        return new(percent, subscription.CurrentPeriodEnd, IsDirectlyReported: false);
    }

    private async Task<string> ReadPublicAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4_000_000) throw new InvalidDataException("公开资源过大");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + count > 4_000_000) throw new InvalidDataException("公开资源过大");
            buffer.Write(block, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
