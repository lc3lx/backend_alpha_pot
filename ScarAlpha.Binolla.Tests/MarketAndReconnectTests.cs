using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Binolla.Tests.Support;
using ScarAlpha.Binolla.Transport;
using Xunit;

namespace ScarAlpha.Binolla.Tests;

public class MarketDataTests
{
    [Fact]
    public async Task Assets_quote_and_candles_come_from_engine_push_data()
    {
        await using var session = await SessionTestFactory.ConnectSimulatedAsync(
            "mkt-1",
            """42["authorization",{"isDemo":true,"token":"SSID_MKT"}]""");

        var assets = await session.GetTradingAssetsAsync();
        Assert.NotEmpty(assets);
        Assert.Contains(assets, a => a.Symbol is "EURUSD" or "GBPUSD_otc");

        var quote = await session.GetLatestQuoteAsync("EURUSD");
        Assert.Equal("EURUSD", quote.Pair);
        Assert.True(quote.Price > 0);

        var history = await session.GetHistoryAsync("EURUSD", 60);
        Assert.Equal("EURUSD", history.Asset);
        Assert.Equal(60, history.Period);
        Assert.NotEmpty(history.Candles);
        Assert.True(history.Candles[0].Close > 0);
    }
}

public class ReconnectTests
{
    [Fact]
    public async Task Drop_then_reconnect_restores_connected_state_without_deadlock()
    {
        FakeWebSocketTransport? current = null;
        var options = new BinollaSessionManagerOptions
        {
            EnableAutoReconnect = true,
            MaxReconnectAttempts = 3,
            DefaultOperationTimeout = TimeSpan.FromSeconds(8),
            EnableChartConnection = false
        };

        await using var session = new BinollaSession("reconn-1", options, () =>
        {
            current = new FakeWebSocketTransport();
            _ = new SimulatedBinollaEndpoint(current);
            return current;
        });

        var reconnected = 0;
        session.OnReconnected += () => Interlocked.Increment(ref reconnected);
        session.OnConnectionLost += () => { };

        var connectTask = session.ConnectAsync("""42["authorization",{"isDemo":true,"token":"SSID_RECONN"}]""");
        await Task.Delay(30);
        current!.InjectServerMessage("""0{"sid":"a"}""");
        await connectTask;

        Assert.Equal(SessionLifecycleState.Connected, session.Lifecycle);

        await current.SimulateDropAsync(new Exception("network"));
        await Task.Delay(100);
        Assert.True(
            session.Lifecycle is SessionLifecycleState.Disconnected or SessionLifecycleState.Reconnecting
                or SessionLifecycleState.Connecting or SessionLifecycleState.Reconnected or SessionLifecycleState.Connected);

        // Drive reconnect handshake on the new transport created by auto-reconnect.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (current is { IsConnected: true } &&
                session.Lifecycle is SessionLifecycleState.Connecting or SessionLifecycleState.Reconnecting)
            {
                current.InjectServerMessage("""0{"sid":"b"}""");
            }

            if (session.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
                break;

            await Task.Delay(50);
        }

        Assert.True(
            session.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected,
            $"Expected reconnected, got {session.Lifecycle}");

        var balance = await session.GetBalanceAsync();
        Assert.True(balance.DemoBalance > 0);
        // OnReconnected is best-effort once per successful AttemptReconnectAsync (not required for correctness).
    }
}
