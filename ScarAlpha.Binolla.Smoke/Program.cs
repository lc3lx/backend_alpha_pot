using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Session;

namespace ScarAlpha.Binolla.Smoke;

/// <summary>
/// Live Demo smoke against real Binolla (optional).
/// Set BINOLLA_SSID env var. Never hardcode or print the SSID.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var ssid = Environment.GetEnvironmentVariable("BINOLLA_SSID");
        if (string.IsNullOrWhiteSpace(ssid))
        {
            Console.WriteLine("BINOLLA_SSID is not set.");
            Console.WriteLine("Export the full authorization frame from browser WS messages, then:");
            Console.WriteLine("  set BINOLLA_SSID=... && dotnet run --project ScarAlpha.Binolla.Smoke");
            return 2;
        }

        var placeTrade = args.Any(a => a.Equals("--trade", StringComparison.OrdinalIgnoreCase))
                         || string.Equals(Environment.GetEnvironmentVariable("BINOLLA_SMOKE_TRADE"), "1", StringComparison.Ordinal);

        var options = new BinollaSessionManagerOptions
        {
            EnableAutoReconnect = false,
            EnableChartConnection = false,
            DefaultOperationTimeout = TimeSpan.FromSeconds(45),
            PlaceOrderTimeout = TimeSpan.FromSeconds(30),
            OutcomeTimeout = TimeSpan.FromMinutes(3)
        };

        await using var session = new BinollaSession("smoke-user", options);

        Console.WriteLine("Connecting (Demo)...");
        try
        {
            await session.ConnectAsync(ssid);
            Console.WriteLine($"State: {session.Lifecycle}");

            var balance = await session.GetBalanceAsync();
            Console.WriteLine($"Demo balance: {balance.DemoBalance} | Type: {balance.CurrentType}");
            if (balance.CurrentType != AccountType.Demo)
            {
                Console.WriteLine("Refusing to continue — account type is not Demo.");
                return 1;
            }

            var assets = await session.GetTradingAssetsAsync();
            Console.WriteLine($"Open assets: {assets.Count}");
            foreach (var a in assets.Take(5))
                Console.WriteLine($"  {a.Symbol} payout={(a.PayoutPercentage > 0 ? a.PayoutPercentage.ToString() : "n/a")}%");

            var symbol = assets.FirstOrDefault(a => a.Symbol.Contains("EUR", StringComparison.OrdinalIgnoreCase))?.Symbol
                         ?? assets.FirstOrDefault()?.Symbol;
            if (symbol is null)
            {
                Console.WriteLine("No assets available.");
                return 1;
            }

            var quote = await session.GetLatestQuoteAsync(symbol);
            Console.WriteLine($"Quote {quote.Pair}: {quote.Price}");

            var history = await session.GetHistoryAsync(symbol, 60);
            Console.WriteLine($"Candles: {history.Candles.Count}");

            if (placeTrade)
            {
                Console.WriteLine($"Placing Demo trade on {symbol} amount=1 duration=60...");
                var order = await session.PlaceOrderAsync(symbol, TradeDirection.Call, 1m, 60);
                Console.WriteLine($"Order opened: {order.OrderId}");
                var outcome = await session.WaitOutcomeAsync(order.OrderId);
                Console.WriteLine($"Outcome: {outcome.Result} pnl={outcome.ProfitLoss}");
            }
            else
            {
                Console.WriteLine("Skipping live order (pass --trade or BINOLLA_SMOKE_TRADE=1 to place one Demo trade).");
            }

            Console.WriteLine("Smoke OK.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Smoke failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }
}
