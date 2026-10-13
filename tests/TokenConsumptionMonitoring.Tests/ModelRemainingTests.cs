using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.QuotaReferences;
using TokenConsumptionMonitoring.UI.Diagnostics;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class ModelRemainingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T12:00:00Z");
    private static PageConfigRecord Page() => new() { Id = "test", BaseUrl = "https://api.commandcode.ai",
        QuotaReferenceModelId = "commandcode:model", QuotaReferencePlanId = "goat" };
    private static ModelQuotaCatalog Catalog() => new("commandcode", Now, "test",
        [new("commandcode:model", "Model", "goat", "GOAT", 60, false, "", "https://commandcode.ai/docs/plans/goat")],
        ConversionRules: [new("commandcode:model", "goat", "individual-goat", 60m / 70m, 70, Now.AddHours(1), "https://commandcode.ai/docs/resources/pricing-limits")]);
    private static CapabilitySnapshot Snapshot()
    {
        var usage = new CommandCodeUsageClient.AccountUsage(null,
            new(new(true, new(1, 5, 4, Now.AddHours(1)), new(2, 15, 13, Now.AddDays(1))), 35),
            new("active", Now.AddDays(-1), Now.AddDays(10), "individual-goat", 1));
        var result = CommandCodeAllowanceWindowMethod.BuildResult(usage, new(CredentialClass.ApiKey, "commandcode"),
            new("commandcode", "test", "commandcode.allowance-window.compat", "https://api.commandcode.ai/alpha"));
        return new() { Metadata = new("test", "fp", Now, null, RefreshReason.Poll), Status = SnapshotStatus.Success,
            Capabilities = result.Capabilities.Select(v => v with { FetchedAt = Now, ExpiresAt = Now.AddMinutes(5) }).Select(v => v is RollingWindowValue w ? w with { Percent = null } : v).ToArray() };
    }

    [Fact]
    public void SharedMonthlyAndWindowPoolsUseModelFactorAndMinimumWithoutChangingFacts()
    {
        var snapshot = Snapshot();
        var result = ModelRemainingEstimator.Estimate(Page(), Catalog(), snapshot, Now);
        Assert.Null(result.Reason);
        Assert.Equal(30m, decimal.Round(result.Amounts["月度剩余"], 10));
        Assert.Equal(decimal.Round(4m * 60 / 70, 10), decimal.Round(result.Amounts["当前套餐可用"], 10));
        Assert.Equal(35m, snapshot.Balances.Single().Balance);
        Assert.All(snapshot.Capabilities, v => Assert.False(v.IsEstimated));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("stale")]
    [InlineData("reset")]
    [InlineData("expired-rule")]
    [InlineData("period")]
    [InlineData("quantity")]
    [InlineData("unlimited")]
    [InlineData("negative")]
    [InlineData("grant")]
    [InlineData("provider")]
    [InlineData("free")]
    public void UnprovenOrExpiredEvidenceCannotProduceAnAmount(string problem)
    {
        var page = Page(); var catalog = Catalog(); var snapshot = Snapshot();
        snapshot = problem switch
        {
            "stale" => snapshot with { Status = SnapshotStatus.Stale },
            "missing" => snapshot with { Capabilities = snapshot.Capabilities.Skip(1).ToArray() },
            "reset" => snapshot with { Capabilities = snapshot.Capabilities.Select(v => v is RollingWindowValue w ? w with { ResetsAt = Now } : v).ToArray() },
            _ => snapshot with { Capabilities = snapshot.Capabilities.Select(v => v with { SharedQuota = problem switch
                {
                    "plan" => v.SharedQuota! with { AccountPlanId = "individual-max" },
                    "period" => v.SharedQuota! with { PeriodEnd = Now },
                    "quantity" => v.SharedQuota! with { Quantity = 2 },
                    "unlimited" => v.SharedQuota! with { WindowsLimited = false },
                    "negative" => v.SharedQuota! with { Remaining = -1 },
                    "grant" => v.SharedQuota! with { MonthlyGranted = 140 },
                    _ => v.SharedQuota
                } }).ToArray() }
        };
        if (problem == "expired-rule") catalog = catalog with { ConversionRules = [catalog.ConversionRules![0] with { ValidUntil = Now }] };
        if (problem == "provider") catalog = catalog with { Provider = "opendesign" };
        if (problem == "free") catalog = catalog with { Entries = [catalog.Entries[0] with { IsFree = true, MonthlyUsd = null }] };
        Assert.Empty(ModelRemainingEstimator.Estimate(page, catalog, snapshot, Now).Amounts);
    }

    [Fact]
    public void ExplicitPercentSuppressesWindowConversionButNotMonthlyWithoutPercent()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Capabilities = snapshot.Capabilities.Select(v => v is RollingWindowValue w
            ? w with { Percent = 0, SharedQuota = null } : v).ToArray() };
        var result = ModelRemainingEstimator.Estimate(Page(), Catalog(), snapshot, Now);
        Assert.Single(result.Amounts);
        Assert.Contains("月度剩余", result.Amounts.Keys);
        Assert.DoesNotContain("当前套餐可用", result.Amounts.Keys);
        Assert.All(snapshot.Windows, w => Assert.Equal(0, w.Percent));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(100)]
    public void AllPercentWindowsNeedNoPricingAndProduceNoDollarLabel(int percent)
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Capabilities = snapshot.Windows.Select(w => w with { Percent = percent, SharedQuota = null }).ToArray() };
        var result = ModelRemainingEstimator.Estimate(Page(), null, snapshot, Now);
        Assert.Empty(result.Amounts);
        Assert.Null(result.Reason);
        Assert.Empty(result.Label);
    }

    [Fact]
    public void MissingWindowDoesNotCreateCombinedAvailability()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Capabilities = snapshot.Capabilities.Skip(1).ToArray() };
        var result = ModelRemainingEstimator.Estimate(Page(), Catalog(), snapshot, Now);
        Assert.Contains("月度剩余", result.Amounts.Keys);
        Assert.DoesNotContain("当前套餐可用", result.Amounts.Keys);
    }

    [Fact]
    public void ZeroRemainingIsRealZeroAndDoesNotBecomeUnknown()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Capabilities = snapshot.Capabilities.Select(v => v with { SharedQuota = v.SharedQuota! with { Remaining = 0 } }).ToArray() };
        Assert.Equal(0, ModelRemainingEstimator.Estimate(Page(), Catalog(), snapshot, Now).Amounts["当前套餐可用"]);
        Assert.Empty(ModelRemainingEstimator.Estimate(Page(), Catalog(), snapshot, Now.AddMinutes(5)).Amounts);
    }

    [Fact]
    public void PricingStepsUseEffectiveBudgetAndStopAtNextBoundary()
    {
        var models = """
            {"models":[{"name":"Model","planBudgetUsd":{"goat":60},"planAllowanceUsd":{"goat":100},"planBudgetSteps":[
            {"at":"2026-10-14T12:00:00Z","planBudgetUsd":{"goat":100},"planAllowanceUsd":{"goat":100}},
            {"at":"2026-10-17T12:00:00Z","planBudgetUsd":{"goat":80},"planAllowanceUsd":{"goat":80}}]}]}
            """;
        var html = "self.__next_f.push([1," + JsonSerializer.Serialize(models) + "])";
        const string tiers = """{id:"individual-goat",name:"GOAT",limits:{premiumCredits:0,opensourceCredits:70,totalCredits:70,}}""";
        var entries = new[] { Catalog().Entries[0] with { MonthlyUsd = 100 } };
        var before = QuotaConversionParser.CommandCode(html, tiers, entries, Now).Single();
        Assert.Equal(60m / 70m, before.UsdPerPoolUnit);
        Assert.Equal(Now.AddDays(1), before.ValidUntil);
        var after = QuotaConversionParser.CommandCode(html, tiers, entries, Now.AddDays(1)).Single();
        Assert.Equal(100m / 70m, after.UsdPerPoolUnit);
        Assert.Empty(QuotaConversionParser.CommandCode(html, tiers, entries, Now.AddDays(4)));
        Assert.Equal(80m / 70m, QuotaConversionParser.CommandCode(html, tiers,
            [entries[0] with { MonthlyUsd = 80 }], Now.AddDays(4)).Single().UsdPerPoolUnit);
        Assert.Throws<InvalidDataException>(() => QuotaConversionParser.CommandCode(html + html, tiers, entries, Now));
    }

    [Fact]
    public void OpenDesignUsesAccountUnitAndSeparateWindowsNeverMonthly()
    {
        var page = new PageConfigRecord { Id = "test", BaseUrl = "https://amr-link.open-design.ai/v1", OpenDesignWorkspaceId = "ws",
            QuotaReferenceModelId = "opendesign:model", QuotaReferencePlanId = "go" };
        var entry = new ModelQuotaReference("opendesign:model", "Model", "go", "Go", 120, false, "", OfficialQuotaCatalogService.OpenDesignPricingUrl);
        var rules = QuotaConversionParser.OpenDesign("""{name:"Model",icon:a,iconClass:"brand",multiplier:.5,weeklyRequests:{go:1}}""", [entry], Now);
        var catalog = new ModelQuotaCatalog("opendesign", Now, "test", [entry], ConversionRules: rules);
        using var document = JsonDocument.Parse("""
            {"status":"ready","context":{"workspaceId":"ws","workspaceMemberId":"m"},"creditsPerUsd":10000,
             "body":{"eligible":true,"tier":"go","windows":[
             {"policyId":"five","durationSeconds":18000,"limitCredits":"50000","remainingCredits":"50000","resetsAt":null},
             {"policyId":"week","durationSeconds":604800,"limitCredits":"150000","remainingCredits":"150000","resetsAt":null}]}}
            """);
        var actual = OpenDesignConsoleMethod.Parse(document.RootElement, "test", "ws", true, Now);
        var snapshot = Snapshot() with { Capabilities = actual.Capabilities.Select(v => v is RollingWindowValue w ? w with { Percent = null } : v).ToArray() };
        var result = ModelRemainingEstimator.Estimate(page, catalog, snapshot, Now);
        Assert.Equal(10, result.Amounts["当前套餐可用"]);
        Assert.Equal(30, result.Amounts["7 天剩余"]);
        Assert.DoesNotContain("月度剩余", result.Amounts.Keys);
        page.OpenDesignWorkspaceId = "other";
        Assert.Empty(ModelRemainingEstimator.Estimate(page, catalog, snapshot, Now).Amounts);
    }

    [Fact]
    public void CapMinusUsedAndIdleResetAreAcceptedOnlyWithExplicitZeroUsage()
    {
        CommandCodeUsageClient.AccountUsage Usage(double? used) => new(null,
            new(new(true, new(used, 14, null, DateTimeOffset.UnixEpoch), null), 35),
            new("active", Now.AddDays(-1), Now.AddDays(1), "individual-goat", 1));
        MethodQueryResult Read(double? used) => CommandCodeAllowanceWindowMethod.BuildResult(Usage(used),
            CredentialScope.None, new("commandcode", "test", "commandcode.allowance-window.compat", "https://api.commandcode.ai/alpha"));
        var idle = Read(0).Capabilities.OfType<RollingWindowValue>().Single();
        Assert.Null(idle.ResetsAt);
        Assert.Equal(14, idle.SharedQuota!.Remaining);
        var unknown = Read(null).Capabilities.OfType<RollingWindowValue>().Single();
        Assert.Equal(DateTimeOffset.UnixEpoch, unknown.ResetsAt);
        Assert.Null(unknown.SharedQuota!.Remaining);
        var usedWindow = Read(2).Capabilities.OfType<RollingWindowValue>().Single();
        Assert.Equal(12, usedWindow.SharedQuota!.Remaining);
        Assert.Equal(DateTimeOffset.UnixEpoch, usedWindow.ResetsAt);
    }

    [Fact]
    public void RevisionChangeAndExplicitFailureClearPreviouslyComputedAmount()
    {
        var page = Page(); var vm = new QuotaReferenceViewModel();
        var now = DateTimeOffset.UtcNow;
        var catalog = Catalog() with { FetchedAt = now, ConversionRules = [Catalog().ConversionRules![0] with { ValidUntil = now.AddHours(1) }] };
        vm.Update(page, catalog);
        vm.ApplySnapshot(Snapshot() with { Capabilities = Snapshot().Capabilities.Select(v => v with { FetchedAt = now, ExpiresAt = now.AddMinutes(5),
            SharedQuota = v.SharedQuota! with { PeriodStart = now.AddDays(-1), PeriodEnd = now.AddDays(1) } }).Select(v => v is RollingWindowValue w ? w with { ResetsAt = now.AddHours(1) } : v).ToArray() });
        Assert.Contains("$", vm.RemainingLabel);
        page.Revision++;
        vm.Update(page, catalog);
        Assert.DoesNotContain("$", vm.RemainingLabel);
        vm.Failed(page);
        Assert.DoesNotContain("$", vm.RemainingLabel);
    }
}
