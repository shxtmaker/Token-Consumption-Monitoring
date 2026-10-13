using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;

namespace TokenConsumptionMonitoring.Services.QuotaReferences;

public sealed record ModelRemainingEstimate(IReadOnlyDictionary<string, decimal> Amounts, string? Reason)
{
    public string Label => Amounts.Count == 0 ? Reason is null ? "" : $"剩余额度：暂不可得（{Reason}）"
        : string.Join("\n", Amounts.Select(p => $"{p.Key}（估算）：约 ${p.Value:0.####}"));
}

/// <summary>纯展示投影；共享额度不能跨模型或跨窗口相加，未知数据不得作为零或无限额度。</summary>
public static class ModelRemainingEstimator
{
    public static ModelRemainingEstimate Estimate(PageConfigRecord? page, ModelQuotaCatalog? catalog,
        CapabilitySnapshot? snapshot, DateTimeOffset now)
    {
        ModelRemainingEstimate No(string why) => new(new Dictionary<string, decimal>(), why);
        if (page is null || snapshot is null) return No("等待账户与官网数据");
        var provider = OfficialQuotaCatalogService.ProviderOf(page.BaseUrl);
        if (provider is null || snapshot.Metadata.PageId != page.Id) return No("来源不匹配");
        var method = provider == "opendesign" ? OpenDesignConsoleMethod.WindowsMethodId : "commandcode.allowance-window.compat";
        var available = snapshot.Capabilities.Where(v => v.Source.Provider == provider && v.Source.MethodId == method).ToArray();
        // 已有明确百分比的额度由原窗口行直接显示，不再换算美元或参与美元汇总。
        static bool HasPercent(CapabilityValue value) => value is RollingWindowValue { Percent: >= 0 and <= 100, IsEstimated: false };
        if (available.Length > 0 && available.All(HasPercent)) return new(new Dictionary<string, decimal>(), null);
        if (catalog is null) return No("等待官网数据");
        if (provider != catalog.Provider) return No("来源不匹配");
        if (catalog.IsStale || catalog.FetchedAt > now || now - catalog.FetchedAt >= TimeSpan.FromHours(24)) return No("官网规则已过期");
        if (snapshot.Status is not (SnapshotStatus.Success or SnapshotStatus.SuccessPartial) || snapshot.IsStale)
            return No("账户数据不可用");
        var entry = catalog.Entries.SingleOrDefault(e => e.ModelId == page.QuotaReferenceModelId && e.PlanId == page.QuotaReferencePlanId);
        if (entry is null || entry.IsFree) return No("未提供可换算的模型额度");
        var rules = catalog.ConversionRules?.Where(r => r.ModelId == entry.ModelId && r.PlanId == entry.PlanId).ToArray();
        if (rules is not { Length: 1 } || rules[0].ValidUntil <= now || rules[0].UsdPerPoolUnit <= 0)
            return No("等待有效换算规则");
        var rule = rules[0];
        var values = available.Where(v => !HasPercent(v) && v.SharedQuota is not null).ToArray();
        if (values.Length == 0 || values.Any(v => v.IsEstimated || v.IsExpired(now) || v.FetchedAt > now
            || now - v.FetchedAt >= TimeSpan.FromMinutes(5)
            || v is RollingWindowValue { ResetsAt: { } reset } && reset <= now)) return No("等待最新账户额度");
        if (values.Select(v => v.Source).Distinct().Count() != 1 || values.Select(v => v.CredentialScope).Distinct().Count() != 1)
            return No("账户范围不一致");
        if (values.Any(v => v.SharedQuota!.AccountPlanId != rule.AccountPlanId)) return No("参考套餐与账户不匹配");
        if (provider == "opendesign" && (!OpenDesignConsoleMethod.TryGetWorkspace(page, out var workspace)
            || values.Any(v => v.Source.Account != workspace || v.CredentialScope.Team != workspace))) return No("工作空间不匹配");
        var facts = values.Select(v => v.SharedQuota!).ToArray();
        if (facts.GroupBy(f => f.Period).Any(g => g.Count() != 1)) return No("额度窗口不唯一");
        var expected = provider == "commandcode" ? new[] { "monthly", "commandcode.fiveHour", "commandcode.weekly" }
            : rule.AccountPlanId == "go" ? new[] { "18000", "604800" } : new[] { "604800" };
        if (facts.Any(f => !expected.Contains(f.Period))) return No("额度窗口不匹配");
        if (facts.Any(f => f.Remaining is null or < 0 || f.Period != "monthly" && (f.Limit is null or <= 0 || f.Remaining > f.Limit)))
            return No("额度数值暂不支持换算");
        if (provider == "commandcode" && (rule.StandardMonthlyPool is not > 0 || facts.Any(f => f.SubscriptionStatus != "active"
            || f.Quantity != 1 || f.PeriodStart is null || f.PeriodStart > now || f.PeriodEnd is null || f.PeriodEnd <= now
            || f.WindowsLimited != true || f.MonthlyGranted is { } grant && grant != rule.StandardMonthlyPool)
            || facts.SingleOrDefault(f => f.Period == "monthly")?.Remaining > rule.StandardMonthlyPool)) return No("套餐账期或额度规则不匹配");
        if (provider == "opendesign" && (facts.Any(f => f.CreditsPerUsd is null or <= 0)
            || facts.Select(f => f.CreditsPerUsd).Distinct().Count() != 1)) return No("缺少额度单位换算");
        try
        {
            var amounts = new Dictionary<string, decimal>();
            foreach (var fact in facts.OrderBy(f => Array.IndexOf(expected, f.Period)))
            {
                var name = fact.Period switch { "monthly" => "月度剩余", "18000" or "commandcode.fiveHour" => "5 小时剩余", _ => "7 天剩余" };
                amounts.Add(name, fact.Remaining!.Value * rule.UsdPerPoolUnit / (provider == "opendesign" ? fact.CreditsPerUsd!.Value : 1));
            }
            if (!available.Any(HasPercent) && expected.All(p => facts.Any(f => f.Period == p)))
                amounts.Add("当前套餐可用", amounts.Values.Min());
            return new(amounts, null);
        }
        catch (OverflowException) { return No("额度超出换算范围"); }
    }
}
