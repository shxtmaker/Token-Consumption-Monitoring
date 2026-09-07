using System.Net;
using System.Net.Http;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.Services.Runtime;
using TokenConsumptionMonitoring.Services.Scanning;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class MethodCooldownTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LimitedMethod(Clock clock) : IQueryMethod
    {
        public int Scans;
        public int Queries;
        public QueryMethodDescriptor Describe() => new("limited", SourceKind.AllowanceOrBalance,
            CredentialClass.None, QueryMethodDescriptor.CapabilitiesOf(CapabilityKind.BalanceOrQuota),
            SourceStability.OfficialStable, MethodEnablement.Always, 0, "1");
        public Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
        {
            Scans++;
            return Task.FromResult(MethodSupport.NotAvailable(Describe(), CandidateStatus.RateLimited, "limited")
                with { Failure = new FailureInfo(CandidateStatus.RateLimited, "limited", clock.Now,
                    RetryAt: clock.Now.AddMinutes(5)) });
        }
        public Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
        {
            Queries++;
            throw new QueryTransportException(CandidateStatus.RateLimited, "limited",
                retryAt: clock.Now.AddMinutes(5));
        }
    }

    [Fact]
    public async Task ScanCooldown_UsesServerDeadlineAndBecomesEligibleAtExpiry()
    {
        var clock = new Clock();
        var method = new LimitedMethod(clock);
        var executor = new MethodExecutor(new MethodResultCache(), clock);
        var page = new PageConfigRecord();
        var context = new ScanContext
        {
            Page = page, ConfigurationFingerprint = "fingerprint", Credentials = new CredentialResolver(page),
            CancellationToken = CancellationToken.None,
        };
        await executor.ScanAsync(page, method, context, CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(2);
        await executor.ScanAsync(page, method, context, CancellationToken.None);
        Assert.Equal(1, method.Scans);
        Assert.False(executor.ConsumeRescan(page.Id));
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.True(executor.ConsumeRescan(page.Id));
        Assert.False(executor.ConsumeRescan(page.Id));
        await executor.ScanAsync(page, method, context, CancellationToken.None);
        Assert.Equal(2, method.Scans);
    }

    [Fact]
    public async Task QueryCooldown_ExplicitRefreshWaitsUntilServerDeadline()
    {
        var clock = new Clock();
        var method = new LimitedMethod(clock);
        var executor = new MethodExecutor(new MethodResultCache(), clock);
        var page = new PageConfigRecord();
        var candidate = MethodSupport.NotAvailable(method.Describe(), CandidateStatus.RateLimited, "limited");
        await executor.ExecuteAsync(page, method, candidate, "fingerprint", CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(2);
        await executor.ExecuteAsync(page, method, candidate, "fingerprint", CancellationToken.None, true);
        Assert.Equal(1, method.Queries);
        clock.Now += TimeSpan.FromMinutes(3);
        await executor.ExecuteAsync(page, method, candidate, "fingerprint", CancellationToken.None, true);
        Assert.Equal(2, method.Queries);
    }

    [Fact]
    public void Transport_ParsesRetryAfterDate()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var expected = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(expected);
        Assert.Equal(expected, QueryTransportException.ReadRetryAt(response));
    }
}
