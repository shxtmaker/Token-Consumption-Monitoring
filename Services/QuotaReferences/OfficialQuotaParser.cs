using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services.QuotaReferences;

/// <summary>只解析官网公开展示的表格，不执行网页脚本，也不从模型品牌或价格推断套餐权限。</summary>
public static class OfficialQuotaParser
{
    private static MatchCollection Matches(string input, string pattern) =>
        Regex.Matches(input, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    private static string Clean(string html)
    {
        html = Regex.Replace(html, @"<(svg|script|style|s|del)\b[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(2))),
            @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(2)).Trim();
    }

    public static string ModelId(string provider, string name) => provider + ":" + name.Trim().ToLowerInvariant();

    public static IReadOnlyList<ModelQuotaReference> ParseOpenDesign(string html)
    {
        var values = new List<ModelQuotaReference>();
        foreach (Match row in Matches(html, "<div\\b[^>]*class=\"dpu-row\"[^>]*>(.*?)</div>"))
        {
            var nameMatch = Matches(row.Groups[1].Value, @"<strong\b[^>]*>(.*?)</strong>").Cast<Match>().SingleOrDefault();
            if (nameMatch is null) continue;
            var name = Clean(nameMatch.Groups[1].Value);
            foreach (Match cell in Matches(row.Groups[1].Value, "<span\\b[^>]*data-usage-value=\"(go|plus|pro|max)\"[^>]*>([^<]*)</span>"))
            {
                var value = Clean(cell.Groups[2].Value);
                if (!value.StartsWith('$')) continue; // 相邻的请求数列不是额度。
                if (!TryAmount(value, out var amount, out var free, out var note)) throw Schema();
                var plan = cell.Groups[1].Value.ToLowerInvariant();
                values.Add(new(ModelId("opendesign", name), name, plan, PlanName(plan), amount, free, note,
                    OfficialQuotaCatalogService.OpenDesignPricingUrl));
            }
        }
        return Validate(values);
    }

    public static IReadOnlyList<ModelQuotaReference> ParseCommandCode(string html, string pagePlan)
    {
        if (pagePlan is not ("go" or "goat" or "pro" or "max")) throw Schema();
        var values = new List<ModelQuotaReference>();
        foreach (Match table in Matches(html, @"<table\b[^>]*>(.*?)</table>"))
        {
            var rows = Matches(table.Groups[1].Value, @"<tr\b[^>]*>(.*?)</tr>").Cast<Match>().ToList();
            if (rows.Count < 2) continue;
            var headers = Cells(rows[0]).Select(Clean).ToList();
            if (headers.Count == 0 || headers[0] != "Model") continue;
            var indexes = headers.Select((h, i) => (h, i)).Where(x =>
                x.h.Equals("Monthly credits", StringComparison.OrdinalIgnoreCase)
                || x.h.Equals("Go credits", StringComparison.OrdinalIgnoreCase)
                || x.h.Equals("GOAT credits", StringComparison.OrdinalIgnoreCase)
                || x.h.Equals("Pro credits", StringComparison.OrdinalIgnoreCase)
                || x.h.Equals("Max 10× credits", StringComparison.OrdinalIgnoreCase)
                || x.h.Equals("Max 20× credits", StringComparison.OrdinalIgnoreCase)).ToList();
            if (indexes.Count == 0) continue;
            foreach (var row in rows.Skip(1))
            {
                var cells = Cells(row);
                if (cells.Count != headers.Count) throw Schema();
                var name = Clean(cells[0]);
                foreach (var (header, index) in indexes)
                {
                    var content = Clean(cells[index]);
                    if (!TryAmount(content, out var amount, out var free, out var note)) throw Schema();
                    var plan = header.StartsWith("Max 20", StringComparison.Ordinal) ? "ultra"
                        : header.StartsWith("Max 10", StringComparison.Ordinal) ? "max" : pagePlan;
                    values.Add(new(ModelId("commandcode", name), name, plan, plan == "max" ? "Max 10×" : PlanName(plan), amount, free, note,
                        OfficialQuotaCatalogService.CommandCodePlanUrl(pagePlan)));
                }
            }
        }
        return Validate(values);
    }

    private static List<string> Cells(Match row) => Matches(row.Groups[1].Value, @"<t[dh]\b[^>]*>(.*?)</t[dh]>")
        .Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    private static bool TryAmount(string value, out decimal? amount, out bool free, out string note)
    {
        amount = null;
        free = value.Equals("Free", StringComparison.OrdinalIgnoreCase);
        note = "";
        if (free) return true;
        var money = Matches(value, @"\$\s*([0-9]+(?:,[0-9]{3})*(?:\.[0-9]+)?)");
        if (money.Count != 1 || !decimal.TryParse(money[0].Groups[1].Value,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number)
            || number < 0 || number > 1_000_000) return false;
        amount = number;
        note = (value[..money[0].Index] + value[(money[0].Index + money[0].Length)..]).Trim();
        return note.Length <= 160;
    }

    private static IReadOnlyList<ModelQuotaReference> Validate(List<ModelQuotaReference> entries)
    {
        if (entries.Count == 0 || entries.Count > 3000 || entries.Any(e => e.ModelName.Length is 0 or > 160)) throw Schema();
        // 一处重复且数值不同不能静默取首项，避免官网改版时展示错误参考。
        var groups = entries.GroupBy(e => (e.ModelId, e.PlanId));
        if (groups.Any(g => g.Distinct().Count() != 1)) throw Schema();
        return groups.Select(g => g.First()).OrderBy(e => e.ModelName, StringComparer.Ordinal).ThenBy(e => e.PlanId).ToList();
    }

    public static string PlanName(string plan) => plan switch
    {
        "go" => "Go", "goat" => "GOAT", "plus" => "Plus", "pro" => "Pro",
        "max" => "Max", "ultra" => "Max 20×", _ => plan,
    };

    private static InvalidDataException Schema() => new("官网额度表结构已变化，暂时无法同步参考额度");
}
