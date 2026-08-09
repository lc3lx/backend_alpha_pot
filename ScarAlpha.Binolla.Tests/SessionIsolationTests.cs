using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Binolla.Tests.Support;
using Xunit;

namespace ScarAlpha.Binolla.Tests;

public class SessionIsolationTests
{
    [Fact]
    public async Task Two_sessions_are_fully_isolated()
    {
        await using var sessionA = await SessionTestFactory.ConnectSimulatedAsync(
            "user-A",
            """42["authorization",{"isDemo":true,"token":"SSID_A_TOKEN"}]""",
            demo: 1111m,
            real: 11m);

        await using var sessionB = await SessionTestFactory.ConnectSimulatedAsync(
            "user-B",
            """42["authorization",{"isDemo":true,"token":"SSID_B_TOKEN"}]""",
            demo: 2222m,
            real: 22m);

        Assert.Equal(SessionLifecycleState.Connected, sessionA.Lifecycle);
        Assert.Equal(SessionLifecycleState.Connected, sessionB.Lifecycle);

        var balA = await sessionA.GetBalanceAsync();
        var balB = await sessionB.GetBalanceAsync();

        Assert.Equal(1111m, balA.DemoBalance);
        Assert.Equal(2222m, balB.DemoBalance);
        Assert.NotEqual(balA.DemoBalance, balB.DemoBalance);

        var orderA = await sessionA.PlaceOrderAsync("EURUSD", TradeDirection.Call, 10m, 60);
        var orderB = await sessionB.PlaceOrderAsync("EURUSD", TradeDirection.Put, 20m, 60);

        Assert.NotEqual(orderA.OrderId, orderB.OrderId);
        Assert.Equal(10m, orderA.Amount);
        Assert.Equal(20m, orderB.Amount);
        Assert.Equal(TradeDirection.Call, orderA.Direction);
        Assert.Equal(TradeDirection.Put, orderB.Direction);

        await sessionA.ChangeAccountAsync(AccountType.Real);
        Assert.Equal(AccountType.Real, sessionA.State.AccountType);
        Assert.Equal(AccountType.Demo, sessionB.State.AccountType);
    }

    [Fact]
    public async Task Five_concurrent_orders_in_one_session_do_not_cross_talk()
    {
        await using var session = await SessionTestFactory.ConnectSimulatedAsync(
            "user-concurrent",
            """42["authorization",{"isDemo":true,"token":"SSID_CONCURRENT"}]""",
            demo: 5000m,
            autoCloseOrders: true);

        var tasks = Enumerable.Range(0, 5)
            .Select(i => session.PlaceOrderAsync(
                "EURUSD",
                i % 2 == 0 ? TradeDirection.Call : TradeDirection.Put,
                amount: 10m + i,
                durationSeconds: 60))
            .ToArray();

        var orders = await Task.WhenAll(tasks);

        Assert.Equal(5, orders.Length);
        Assert.Equal(5, orders.Select(o => o.OrderId).Distinct().Count());

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(10m + i, orders[i].Amount);
            Assert.Equal(i % 2 == 0 ? TradeDirection.Call : TradeDirection.Put, orders[i].Direction);
        }

        var outcomeTasks = orders.Select(o => session.WaitOutcomeAsync(o.OrderId)).ToArray();
        var outcomes = await Task.WhenAll(outcomeTasks);

        Assert.Equal(5, outcomes.Length);
        foreach (var order in orders)
        {
            var outcome = outcomes.Single(x => x.OrderId == order.OrderId);
            Assert.Equal(order.OrderId, outcome.OrderId);
            Assert.True(outcome.ProfitLoss > 0);
        }

        Assert.Equal(0, session.Orders.PendingOpenCount);
        Assert.Equal(0, session.Orders.PendingOutcomeCount);
    }

    [Fact]
    public async Task Ten_total_concurrent_orders_across_two_sessions_have_zero_cross_talk()
    {
        await using var a = await SessionTestFactory.ConnectSimulatedAsync(
            "user-A2",
            """42["authorization",{"isDemo":true,"token":"SSID_A2"}]""",
            demo: 9000m,
            autoCloseOrders: true);

        await using var b = await SessionTestFactory.ConnectSimulatedAsync(
            "user-B2",
            """42["authorization",{"isDemo":true,"token":"SSID_B2"}]""",
            demo: 8000m,
            autoCloseOrders: true);

        var tasksA = Enumerable.Range(0, 5)
            .Select(i => a.PlaceOrderAsync("EURUSD", TradeDirection.Call, 5 + i, 60));
        var tasksB = Enumerable.Range(0, 5)
            .Select(i => b.PlaceOrderAsync("GBPUSD_otc", TradeDirection.Put, 50 + i, 60));

        var all = await Task.WhenAll(tasksA.Concat(tasksB));
        Assert.Equal(10, all.Length);
        Assert.Equal(10, all.Select(x => x.OrderId).Distinct().Count());

        Assert.All(all.Where(o => o.Asset == "EURUSD"), o => Assert.Equal(TradeDirection.Call, o.Direction));
        Assert.All(all.Where(o => o.Asset == "GBPUSD_otc"), o => Assert.Equal(TradeDirection.Put, o.Direction));
    }
}
