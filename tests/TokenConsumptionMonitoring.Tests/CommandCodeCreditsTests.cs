using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.QueryMethods;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

/// <summary>
/// Command Code goat 套餐月度剩余 credits：经余额能力透出。
/// monthlyCredits 是服务端直接返回的账务事实（随用量递减的剩余值）；
/// 月度上限/已用无接口提供，不推导，因此 Balance 无 Limit、不触发告警。
/// </summary>
public sealed class CommandCodeCreditsTests
{
    private static readonly SourceIdentity Source = new(
        "commandcode", "api-key", "commandcode.allowance-window.compat", "https://api.commandcode.ai/alpha");

    private static readonly CredentialScope Scope = new(CredentialClass.ApiKey, "commandcode");

    /// <summary>goat 套餐真实响应样本（2026-09-03 抓取）。</summary>
    private static readonly CommandCodeUsageClient.AccountUsage GoatUsage = new(
        null,
        new CommandCodeUsageClient.Credits(
            new CommandCodeUsageClient.WindowLimits(true,
                new CommandCodeUsageClient.WindowLimit(0.06846539, 14, 13.93153461, DateTimeOffset.FromUnixTimeMilliseconds(1788421745492)),
                new CommandCodeUsageClient.WindowLimit(6.475987145, 35, 28.524012855, DateTimeOffset.FromUnixTimeMilliseconds(1788523675154))),
            63.2129584706),
        new CommandCodeUsageClient.SubscriptionData("active",
            DateTimeOffset.Parse("2026-08-19T10:32:37.000Z"),
            DateTimeOffset.Parse("2026-09-19T10:32:37.000Z")));

    [Fact]
    public void BuildResult_EmitsMonthlyBalanceWithoutLimitAndWindows()
    {
        var result = CommandCodeAllowanceWindowMethod.BuildResult(GoatUsage, Scope, Source);

        Assert.Equal(SnapshotStatus.Success, result.Status);
        var windowKeys = result.Capabilities.OfType<RollingWindowValue>().Select(w => w.WindowKey).ToHashSet();
        Assert.Equal(new HashSet<string> { "commandcode.fiveHour", "commandcode.weekly" }, windowKeys);

        var monthly = Assert.Single(result.Capabilities.OfType<BalanceQuotaValue>());
        Assert.Equal(63.21m, Math.Round(monthly.Balance!.Value, 2));
        Assert.Equal("USD", monthly.Currency);
        Assert.Equal("credits", monthly.Unit);
        Assert.Null(monthly.Limit);
        Assert.Null(monthly.Used);
        Assert.Null(monthly.Remaining);
        Assert.True(monthly.IsPrivate);
    }

    [Fact]
    public void BalanceLabel_RendersMonthlyRemainingInUsd()
    {
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("p", "fp", DateTimeOffset.UtcNow, "m", RefreshReason.Poll),
            Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, Source, Scope, Coverage.Unknown, DateTimeOffset.UtcNow,
                    1, true, false, 63.21m, null, null, null, "USD", "credits"),
            },
        };
        var viewModel = new UI.Diagnostics.CapabilitySnapshotViewModel();

        viewModel.Update(snapshot, showDailyUsage: true);

        Assert.True(viewModel.HasBalance);
        Assert.Equal("余额 63.21 USD", viewModel.BalanceLabel);
    }

    [Fact]
    public void EvaluateBalance_WithoutLimit_DoesNotAlert()
    {
        var settings = new AppSettings { WarnPercent = 80, CriticalPercent = 95 };
        var toastCount = 0;
        var service = new AlertService(settings, _ => toastCount++);
        var result = CommandCodeAllowanceWindowMethod.BuildResult(GoatUsage, Scope, Source);
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("p", "fp", DateTimeOffset.UtcNow, "m", RefreshReason.Poll),
            Status = SnapshotStatus.Success,
            Capabilities = result.Capabilities,
        };

        var alert = service.EvaluateSnapshot(snapshot, "p");

        Assert.Equal(AlertLevel.None, alert.Overall);
        Assert.Equal(0, toastCount);
    }
}
