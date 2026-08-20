using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Persistence;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

/// <summary>Phase 1/2：领域契约可序列化、能力值语义、缓存键与指纹。</summary>
public class ContractTests
{
    private static SourceIdentity Src(string provider = "opencode") =>
        new(provider, "api-key", "test.method", "https://opencode.ai/x");

    [Fact]
    public void CapabilitySnapshot_JsonRoundTrip_KeepsValues()
    {
        var source = Src();
        var scope = new CredentialScope(CredentialClass.ApiKey, "opencode");
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("page1", "fp1", DateTimeOffset.UtcNow, "test.method", RefreshReason.Poll),
            Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
                    Confidence: 1.0, IsPrivate: false, IsEstimated: false,
                    WindowKey: "rolling", WindowName: "5h滚动", Status: "正常",
                    Used: null, Limit: 100_000_000, Remaining: 40_000_000, Percent: 60, ResetsAt: DateTimeOffset.UtcNow.AddHours(2), Unit: "microCents"),
                new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
                    Confidence: 1.0, IsPrivate: false, IsEstimated: false,
                    Balance: 12.5m, Used: null, Limit: null, Remaining: null, Currency: "USD", Unit: null),
            },
        };

        var json = JsonSerializer.Serialize(snapshot);
        var back = JsonSerializer.Deserialize<CapabilitySnapshot>(json);

        Assert.NotNull(back);
        Assert.Equal("page1", back!.Metadata.PageId);
        Assert.Equal(SnapshotStatus.Success, back.Status);
        Assert.Equal(2, back.Capabilities.Count);
        var win = Assert.IsType<RollingWindowValue>(back.Windows.Single());
        Assert.Equal("5h滚动", win.WindowName);
        Assert.Equal(60, win.Percent);
        var bal = Assert.IsType<BalanceQuotaValue>(back.Balances.Single());
        Assert.Equal(12.5m, bal.Balance);
        Assert.Equal("opencode", win.Source.Provider);
    }

    [Fact]
    public void RollingWindow_EffectivePercent_DerivedFromAbsolute_WhenPercentMissing()
    {
        var source = Src();
        var scope = new CredentialScope(CredentialClass.ApiKey);
        var w = new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
            Confidence: 1.0, IsPrivate: false, IsEstimated: false,
            WindowKey: "k", WindowName: "周", Status: null, Used: null, Limit: 100, Remaining: 25, Percent: null, ResetsAt: null, Unit: null);

        Assert.Equal(75, w.EffectivePercent());
    }

    [Fact]
    public void Snapshot_Empty_IsNoData()
    {
        var s = CapabilitySnapshot.Empty("p", "fp");
        Assert.Equal(SnapshotStatus.NoData, s.Status);
        Assert.Empty(s.Capabilities);
    }

    [Fact]
    public void CacheKey_IncludesPageFingerprintMethodAndVersion()
    {
        var k1 = MethodResultCache.MethodKey("p1", "fpA", "m1", "1.0.0");
        var k2 = MethodResultCache.MethodKey("p1", "fpA", "m1", "1.0.1");
        var k3 = MethodResultCache.MethodKey("p1", "fpB", "m1", "1.0.0");
        var k4 = MethodResultCache.MethodKey("p1", "fpA", "m2", "1.0.0");

        Assert.NotEqual(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.NotEqual(k1, k4);
    }

    [Fact]
    public void CacheKey_QualifiedCacheKeyPerCapability()
    {
        var k = MethodResultCache.KeyFor("p", "fp", "m", "1.0.0", CapabilityKind.RollingWindow);
        Assert.Contains("RollingWindow", k);
    }

    [Fact]
    public void Fingerprint_StableForSameConfig_ChangesOnEndpoint()
    {
        var page = new PageConfigRecord
        {
            Id = "p", Name = "n", BaseUrl = "https://opencode.ai", Protocol = "ChatCompletions",
            CredentialRef = CredentialReference.LegacyPageApiKey("p"),
        };
        var builder = new FingerprintBuilder(new[] { "1.0.0", "1.0.0" });

        var f1 = builder.Build(page, localRecordsPresent: false);
        var same = builder.Build(page, localRecordsPresent: false);
        page.BaseUrl = "https://opencode.ai/v2";
        var changed = builder.Build(page, localRecordsPresent: false);
        page.BaseUrl = "https://opencode.ai";
        var localChanged = builder.Build(page, localRecordsPresent: true);

        Assert.Equal(f1, same);            // 名称/重复版本/相同配置 → 指纹稳定
        Assert.NotEqual(f1, changed);      // 端点变化 → 指纹变化
        Assert.NotEqual(f1, localChanged); // 本地来源状态变化 → 指纹变化
        Assert.False(f1.Contains("opencode", StringComparison.OrdinalIgnoreCase));  // 指纹不含端点明文
    }

    [Fact]
    public void PageMethodState_JsonRoundTrip_WithCandidateAndDescriptor()
    {
        var d = new QueryMethodDescriptor(
            "deepseek.balance.api-key", SourceKind.AllowanceOrBalance, CredentialClass.ApiKey,
            QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
            SourceStability.OfficialStable, MethodEnablement.Always, 40, "1.0.0");
        var candidate = MethodSupport.Available(d, new CredentialScope(CredentialClass.ApiKey, "deepseek"),
            Coverage.Unknown, new[] { DetectionEvidence.Field("/user/balance") },
            source: new SourceIdentity("deepseek", "api-key", d.MethodId, "url"));
        var state = new PageMethodState
        {
            PageId = "page-1",
            Fingerprint = "fp",
            Candidates = new List<MethodCandidate> { candidate },
            SelectedMethodId = d.MethodId,
            SelectionStatus = CandidateStatus.Available,
        };

        var json = JsonSerializer.Serialize(state);
        var back = JsonSerializer.Deserialize<PageMethodState>(json);

        Assert.NotNull(back);
        Assert.Equal("page-1", back!.PageId);
        Assert.Equal(d.MethodId, back.SelectedMethodId);
        var c = Assert.Single(back.Candidates);
        Assert.Equal(d.MethodId, c.Method.MethodId);
        Assert.True(c.IsAvailable);
        Assert.Equal(CapabilityKind.BalanceOrQuota, Assert.Single(c.Method.Capabilities));
    }

    [Fact]
    public void ReportedUsage_And_EstimatedCost_AreDistinctCapabilities()
    {
        var source = Src();
        var scope = new CredentialScope(CredentialClass.ApiKey);
        var snapshot = new CapabilitySnapshot
        {
            Metadata = new SnapshotMetadata("p", "fp", DateTimeOffset.UtcNow, "m", RefreshReason.Poll),
            Status = SnapshotStatus.Success,
            Capabilities = new CapabilityValue[]
            {
                new ReportedUsageValue(CapabilityKind.ReportedUsage, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
                    Confidence: 1.0, IsPrivate: false, IsEstimated: false,
                    TotalTokens: 1000, TotalRequests: 3, Models: new[] { new ModelUsageRow("deepseek-v4", 1000) }),
                new EstimatedCostValue(CapabilityKind.EstimatedCost, source, scope, Coverage.Unknown, DateTimeOffset.UtcNow,
                    Confidence: 1.0, IsPrivate: false, IsEstimated: true,
                    Amount: 1.23m, Currency: "USD", PricingSource: "test", PricingVersion: "v1"),
            },
        };

        Assert.Single(snapshot.ReportedUsages);
        Assert.Single(snapshot.EstimatedCosts);
        Assert.Empty(snapshot.ReportedCosts);
        Assert.Equal(1000, snapshot.ReportedUsages.Single().TotalTokens);
        Assert.True(snapshot.EstimatedCosts.Single().IsEstimated);
    }
}
