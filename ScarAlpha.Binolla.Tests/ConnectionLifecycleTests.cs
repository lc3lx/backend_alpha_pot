using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Binolla.Tests.Support;
using ScarAlpha.Binolla.Transport;
using Xunit;

namespace ScarAlpha.Binolla.Tests;

public class ConnectionLifecycleTests
{
    [Fact]
    public async Task Connection_loss_fails_pending_ops_without_deadlock()
    {
        await using var session = await SessionTestFactory.ConnectSimulatedAsync(
            "user-drop",
            """42["authorization",{"isDemo":true,"token":"SSID_DROP"}]""",
            autoCloseOrders: false,
            enableReconnect: false);

        var lost = false;
        session.OnConnectionLost += () => lost = true;

        var order = await session.PlaceOrderAsync("EURUSD", TradeDirection.Call, 1m, 60);
        var waitTask = session.WaitOutcomeAsync(order.OrderId);

        var transport = session.TradingTransport as FakeWebSocketTransport;
        Assert.NotNull(transport);
        await transport!.SimulateDropAsync(new Exception("drop"));

        await Task.Delay(100);
        Assert.True(lost);
        Assert.Equal(SessionLifecycleState.Disconnected, session.Lifecycle);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await waitTask);
        Assert.True(ex is BinollaConnectionException || ex is AggregateException);

        Assert.Equal(0, session.Orders.PendingOutcomeCount);
    }

    [Fact]
    public async Task Invalid_ssid_does_not_hang_and_reports_auth_failure()
    {
        var options = new BinollaSessionManagerOptions
        {
            EnableAutoReconnect = false,
            DefaultOperationTimeout = TimeSpan.FromSeconds(3),
            EnableChartConnection = false
        };

        FakeWebSocketTransport transport = null!;
        var session = new BinollaSession("user-bad", options, () =>
        {
            transport = new FakeWebSocketTransport();
            _ = new SimulatedBinollaEndpoint(transport, forceFailSsidMarker: "INVALID");
            return transport;
        });

        await using (session)
        {
            var connectTask = session.ConnectAsync(
                """42["authorization",{"isDemo":true,"token":"INVALID_TOKEN"}]""");

            await Task.Delay(20);
            transport.InjectServerMessage("""0{"sid":"x"}""");

            var ex = await Assert.ThrowsAsync<BinollaAuthenticationException>(() => connectTask);
            Assert.Contains("not authorized", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SessionLifecycleState.AuthenticationFailed, session.Lifecycle);
        }
    }

    [Fact]
    public async Task Cancellation_cleans_open_and_outcome_waiters()
    {
        await using var session = await SessionTestFactory.ConnectSimulatedAsync(
            "user-cancel",
            """42["authorization",{"isDemo":true,"token":"SSID_CANCEL"}]""",
            autoCloseOrders: false);

        using var cts = new CancellationTokenSource();
        var order = await session.PlaceOrderAsync("EURUSD", TradeDirection.Call, 3m, 60);

        var wait = session.WaitOutcomeAsync(order.OrderId, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);

        await Task.Delay(50);
        Assert.Equal(0, session.Orders.PendingOutcomeCount);
    }

    [Fact]
    public async Task Connect_cancellation_does_not_hang()
    {
        var options = new BinollaSessionManagerOptions
        {
            EnableAutoReconnect = false,
            DefaultOperationTimeout = TimeSpan.FromSeconds(10),
            EnableChartConnection = false
        };

        FakeWebSocketTransport? transport = null;
        var session = new BinollaSession("user-cancel-connect", options, () =>
        {
            transport = new FakeWebSocketTransport();
            return transport;
        });

        await using (session)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => session.ConnectAsync("""42["authorization",{"token":"x"}]""", cts.Token));
        }
    }
}
