using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using TokenConsumptionMonitoring.UI.Diagnostics;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class ContractTests
{
    private static SourceIdentity Source(string id = "method") =>
        new("test", "account", id, "https://test.invalid/usage");

    [Fact]
    public void CapabilityItemKey_KeepsMultipleWindowsAndCurrencies()
    {
        var source = Source();
        var scope = new CredentialScope(CredentialClass.ApiKey, "test");
        var now = DateTimeOffset.UtcNow;
        var windows = new CapabilityValue[]
        {
            new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now,
                1, false, false, "five_hour", "5h", "ok", null, 100, 50, 50, null, "units"),
            new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, now,
                1, false, false, "weekly", "周", "ok", null, 200, 120, 60, null, "units"),
        };
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("p", "fp", now, "method", RefreshReason.Poll),
            Status = SnapshotStatus.Success,
            Capabilities = windows,
        };

        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal(2, snapshot.Windows.Count());

        var balances = windows.Append(
            new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, now,
                1, false, false, 1m, null, null, 1m, "USD", "credits"))
            .Append(new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, now,
                1, false, false, 2m, null, null, 2m, "CNY", "credits"))
            .ToArray();
        var balanceSnapshot = snapshot with { Capabilities = balances };
        Assert.Equal(2, balanceSnapshot.Balances.Count());
    }

    [Fact]
    public void StaleCapability_IsSerializedAndKeepsFetchedAt()
    {
        var fetchedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var value = new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, Source(),
            new CredentialScope(CredentialClass.ApiKey), Coverage.Unknown, fetchedAt,
            1, false, false, 0m, null, null, null, "USD", null)
        { IsStale = true };
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("p", "fp", fetchedAt, "method", RefreshReason.Poll),
            Status = SnapshotStatus.Stale,
            Capabilities = new CapabilityValue[] { value },
        };

        var json = JsonSerializer.Serialize(snapshot);
        var restored = JsonSerializer.Deserialize<CapabilitySnapshot>(json);

        Assert.NotNull(restored);
        Assert.Equal(SnapshotStatus.Stale, restored!.Status);
        Assert.True(restored.Balances.Single().IsStale);
        Assert.Equal(fetchedAt, restored.Metadata.FetchedAt);
    }

    [Fact]
    public void ReportedAndEstimatedCostRemainDifferent()
    {
        var source = Source();
        var scope = new CredentialScope(CredentialClass.ApiKey);
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("p", "fp", DateTimeOffset.UtcNow, "method", RefreshReason.Poll),
            Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new ReportedCostValue(CapabilityKind.ReportedCost, source, scope, Coverage.Unknown,
                    DateTimeOffset.UtcNow, 1, false, false, 0m, "USD"),
                new EstimatedCostValue(CapabilityKind.EstimatedCost, source, scope, Coverage.Unknown,
                    DateTimeOffset.UtcNow, 1, false, true, 1.23m, "USD", "test", "v1"),
            },
        };

        Assert.Single(snapshot.ReportedCosts);
        Assert.Equal(0m, snapshot.ReportedCosts.Single().Amount);
        Assert.Single(snapshot.EstimatedCosts);
    }

    [Fact]
    public void StaleWindow_DoesNotCreateAlert()
    {
        var settings = new AppSettings { WarnPercent = 80, CriticalPercent = 95 };
        var toastCount = 0;
        var service = new AlertService(settings, _ => toastCount++);
        var now = DateTimeOffset.UtcNow;
        var window = new RollingWindowValue(CapabilityKind.RollingWindow, Source(),
            new CredentialScope(CredentialClass.ApiKey), Coverage.Unknown, now,
            1, false, false, "five_hour", "5h", "ok", null, 100, 0, 100, null, "units")
        { IsStale = true };
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("p", "fp", now, "method", RefreshReason.Poll),
            Status = SnapshotStatus.Stale,
            Capabilities = new CapabilityValue[] { window },
        };

        var result = service.EvaluateSnapshot(snapshot, "p");

        Assert.Equal(AlertLevel.None, result.Overall);
        Assert.Equal(AlertLevel.None, Assert.Single(result.Windows).Level);
        Assert.Equal(0, toastCount);
    }

    [Fact]
    public void ZeroReportedUsageAndCost_RemainVisible()
    {
        var now = DateTimeOffset.UtcNow;
        var source = Source();
        var scope = new CredentialScope(CredentialClass.ApiKey);
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("p", "fp", now, "method", RefreshReason.Poll),
            Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new ReportedUsageValue(CapabilityKind.ReportedUsage, source, scope, Coverage.Unknown, now,
                    1, false, false, 0, 0, Array.Empty<ModelUsageRow>()),
                new ReportedCostValue(CapabilityKind.ReportedCost, source, scope, Coverage.Unknown, now,
                    1, false, false, 0m, "USD"),
            },
        };
        var viewModel = new CapabilitySnapshotViewModel();

        viewModel.Update(snapshot, showDailyUsage: true);

        Assert.True(viewModel.HasReportedUsage);
        Assert.True(viewModel.HasReportedCosts);
        Assert.Equal("0", viewModel.ReportedUsageLabel);
        Assert.Equal(0m, Assert.Single(viewModel.ReportedCostRows).Amount);
    }

    [Fact]
    public void AppIdentity_UsesFormalProductLiterals()
    {
        Assert.Equal("TokenConsumptionMonitoring", AppIdentity.ProductName);
        Assert.Equal("TokenConsumptionMonitoring", AppIdentity.AssemblyName);
        Assert.Equal("TokenConsumptionMonitoring.exe", AppIdentity.ExecutableName);
        Assert.Equal("TokenConsumptionMonitoring", AppIdentity.DataDirectoryName);
        Assert.Equal("TokenConsumptionMonitoring.ApiKey.page", AppIdentity.ApiKeyTarget("page"));
        Assert.Equal(CredentialRefKind.PageApiKey, CredentialReference.PageApiKey("page").Kind);
        Assert.Equal("TokenConsumptionMonitoring.ApiKey.page", CredentialReference.PageApiKey("page").Target);
    }

    [Fact]
    public void Fingerprint_ChangesForMethodIdentityAndVersionButNotDisplayName()
    {
        var descriptor = new QueryMethodDescriptor(
            "method-a", SourceKind.AllowanceOrBalance, CredentialClass.ApiKey,
            QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
            SourceStability.OfficialStable, MethodEnablement.Always, 10, "1.0.0");
        var page = new PageConfigRecord
        {
            Id = "p",
            Name = "one",
            BaseUrl = "https://example.invalid/api",
            Protocol = "ChatCompletions",
            CredentialRef = CredentialReference.PageApiKey("p"),
        };
        var builder = new FingerprintBuilder(new[] { descriptor });
        var first = builder.Build(page, false);
        page.Name = "two";
        Assert.Equal(first, builder.Build(page, false));

        var changedId = new FingerprintBuilder(new[] { descriptor with { MethodId = "method-b" } });
        Assert.NotEqual(first, changedId.Build(page, false));
    }

    [Fact]
    public void MethodState_DoesNotPersistTemporaryOverride()
    {
        var state = new PageMethodState { PageId = "p", Fingerprint = "fp" };
        var json = JsonSerializer.Serialize(state);
        Assert.DoesNotContain("TemporaryOverride", json, StringComparison.OrdinalIgnoreCase);
    }
}
