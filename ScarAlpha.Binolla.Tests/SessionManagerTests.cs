using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Binolla.Tests.Support;
using ScarAlpha.Binolla.Transport;
using Xunit;

namespace ScarAlpha.Binolla.Tests;

public class SessionManagerTests
{
    [Fact]
    public async Task Manager_isolates_users_and_enforces_max()
    {
        var options = new BinollaSessionManagerOptions
        {
            MaxConcurrentSessions = 2,
            EnableAutoReconnect = false,
            DefaultOperationTimeout = TimeSpan.FromSeconds(5),
            EnableChartConnection = false,
            IdleTimeout = TimeSpan.FromMinutes(30)
        };

        FakeWebSocketTransport? last = null;
        await using var manager = new BinollaSessionManager(options, () =>
        {
            last = new FakeWebSocketTransport();
            _ = new SimulatedBinollaEndpoint(last, demoBalance: 100m)
            {
                AutoCloseOrders = false
            };
            return last;
        });

        async Task<IBinollaClient> ConnectUser(string id, string token)
        {
            var task = manager.GetOrCreateAsync(id, "42[\"authorization\",{\"token\":\"" + token + "\"}]");
            await Task.Delay(15);
            last!.InjectServerMessage("""0{"sid":"m"}""");
            var client = await task;

            // Wait for bootstrap balance
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    var b = await client.GetBalanceAsync();
                    if (b.DemoBalance > 0) return client;
                }
                catch (BinollaTimeoutException)
                {
                    // continue
                }

                await Task.Delay(20);
            }

            return client;
        }

        var u1 = await ConnectUser("u1", "T1");
        var u2 = await ConnectUser("u2", "T2");
        Assert.Equal(2, manager.ActiveSessionCount);

        await Assert.ThrowsAsync<BinollaException>(() => ConnectUser("u3", "T3"));

        Assert.Same(u1, manager.Get("u1"));
        Assert.NotSame(u1, manager.Get("u2"));

        var bal1 = await u1.GetBalanceAsync();
        var bal2 = await u2.GetBalanceAsync();
        Assert.Equal(100m, bal1.DemoBalance);
        Assert.Equal(100m, bal2.DemoBalance);

        await manager.RemoveAsync("u1");
        Assert.Null(manager.Get("u1"));
        Assert.Equal(1, manager.ActiveSessionCount);
    }
}
