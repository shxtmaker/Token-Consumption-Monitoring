using System.Net;
using TokenConsumptionMonitoring.Services;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class CommandCodeMonthlyMeterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T13:00:00Z");
    private static readonly CommandCodeUsageClient.SubscriptionData Subscription =
        new("active", Now.AddDays(-24), Now.AddDays(6), "individual-goat", 1);
    private static readonly CommandCodeMonthlyMeterSource.Tier Tier = new("individual-goat", false, 70);
    private const string Tiers = """
        const x=[{id:"individual-goat",name:"GOAT",type:"individual",price:10,
        limits:{premiumCredits:0,opensourceCredits:70,totalCredits:70,...s("individual-goat")}},
        {id:"teams-pro",name:"Team",type:"org",price:40,
        limits:{premiumCredits:40,opensourceCredits:0,totalCredits:40,...s("teams-pro")}}];
        """;

    [Fact]
    public void MonthlyPercentageUsesOfficialPoolNotSelectedModelBudget()
    {
        var result = CommandCodeMonthlyMeterSource.Normalize(new(null, 41.3), Subscription, Tier, Now);
        Assert.NotNull(result);
        Assert.Equal(41, result.UsedPercent);
        Assert.False(result.IsDirectlyReported);
        Assert.Equal(Subscription.CurrentPeriodEnd, result.ResetsAt);
    }

    [Theory]
    [InlineData(70, 0)]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(80, 0)]
    [InlineData(69.65, 1)]
    public void PercentClampingAndHalfRoundingMatchWebsite(double remaining, int expected)
        => Assert.Equal(expected, CommandCodeMonthlyMeterSource.Normalize(new(null, remaining), Subscription, Tier, Now)!.UsedPercent);

    [Fact]
    public void UnknownValuesOrInvalidPeriodDoNotInventPercent()
    {
        foreach (var remaining in new double?[] { null, double.NaN, double.PositiveInfinity })
            Assert.Null(CommandCodeMonthlyMeterSource.Normalize(new(null, remaining), Subscription, Tier, Now));
        foreach (var subscription in new[] { Subscription with { Status = "past_due" }, Subscription with { PlanId = "unknown" },
            Subscription with { CurrentPeriodEnd = Now }, Subscription with { CurrentPeriodStart = null } })
            Assert.Null(CommandCodeMonthlyMeterSource.Normalize(new(null, 40), subscription, Tier, Now));
    }

    [Fact]
    public void GrantedCreditsAndSeatsFollowWebsitePrecedence()
    {
        Assert.Equal(50, CommandCodeMonthlyMeterSource.Normalize(new(null, 50, 100), Subscription, Tier, Now)!.UsedPercent);
        Assert.Equal(50, CommandCodeMonthlyMeterSource.Normalize(new(null, 35, 10), Subscription, Tier, Now)!.UsedPercent);
        var team = Subscription with { PlanId = "teams-pro", Quantity = 3 };
        var tier = new CommandCodeMonthlyMeterSource.Tier("teams-pro", true, 40);
        Assert.Equal(50, CommandCodeMonthlyMeterSource.Normalize(new(null, 60), team, tier, Now)!.UsedPercent);
        Assert.Equal(50, CommandCodeMonthlyMeterSource.Normalize(new(null, 40, 80), team, tier, Now)!.UsedPercent);
        Assert.Null(CommandCodeMonthlyMeterSource.Normalize(new(null, 40), team with { Quantity = null }, tier, Now));
    }

    [Fact]
    public void PublicTierParserRejectsAmbiguousOrInconsistentData()
    {
        var tiers = CommandCodeMonthlyMeterSource.ParseTiers(Tiers);
        Assert.Equal(70, tiers["individual-goat"].TotalCredits);
        Assert.True(tiers["teams-pro"].IsOrganization);
        Assert.Throws<InvalidDataException>(() => CommandCodeMonthlyMeterSource.ParseTiers(Tiers + Tiers));
        Assert.Throws<InvalidDataException>(() => CommandCodeMonthlyMeterSource.ParseTiers(Tiers.Replace("totalCredits:70", "totalCredits:80")));
    }

    [Fact]
    public async Task PublicRulesHaveNoCredentialAndAreCachedSeparatelyFromAccountData()
    {
        var handler = new PublicHandler();
        using var source = new CommandCodeMonthlyMeterSource(new HttpClient(handler));
        var subscription = Subscription with { CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-1), CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(1) };
        Assert.Equal(41, (await source.ReadAsync(new(null, 41.3), subscription, default))!.UsedPercent);
        Assert.Equal(100, (await source.ReadAsync(new(null, 0), subscription, default))!.UsedPercent);
        Assert.Equal(2, handler.Calls);
        Assert.False(handler.HadAuthorization);
        Assert.All(handler.Urls, u => Assert.Equal("commandcode.ai", u.Host));
    }

    private sealed class PublicHandler : HttpMessageHandler
    {
        public int Calls;
        public bool HadAuthorization;
        public List<Uri> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            HadAuthorization |= request.Headers.Authorization is not null;
            Urls.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                request.RequestUri!.AbsolutePath == "/signin" ? "<script src=\"/assets/plan-tiers-test.js\"></script>" : Tiers) });
        }
    }
}
