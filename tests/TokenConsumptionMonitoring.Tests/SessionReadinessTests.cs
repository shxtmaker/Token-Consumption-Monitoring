using System.Windows.Threading;
using TokenConsumptionMonitoring.Services;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class SessionReadinessTests
{
    [Fact]
    public async Task FetchBeforeWebViewReady_ReturnsImmediatelyAndCanBeCancelled()
    {
        using var session = new DeepSeekSessionService(Dispatcher.CurrentDispatcher);
        using var cancellation = new CancellationTokenSource();
        var pending = session.FetchAsync("/test", cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
