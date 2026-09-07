using TokenConsumptionMonitoring.Services;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class WindowsCredentialTests
{
    [Fact]
    public void IsolatedWindowsCredential_RoundTripsAndDeletes()
    {
        var target = AppIdentity.ProductName + ".Tests." + Guid.NewGuid().ToString("N");
        var secret = Guid.NewGuid().ToString("N");
        var store = new WindowsPageCredentialStore();
        try
        {
            store.Write(target, secret);
            Assert.True(store.TryRead(target, out var actual));
            Assert.True(secret == actual, "临时凭据读回不一致");
            store.Delete(target);
            Assert.False(store.TryRead(target, out _));
        }
        finally { store.Delete(target); }
    }
}
