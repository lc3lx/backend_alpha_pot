using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;
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

        // Binolla's WS handshake usually needs the same cookies the login produced
        // (notably __cf_bm). capture.mjs returns them alongside the token.
        var cookies = Environment.GetEnvironmentVariable("BINOLLA_COOKIES");

        Console.WriteLine($"Connecting (Demo){(string.IsNullOrWhiteSpace(cookies) ? "" : " with cookies")}...");
        try
        {
            await session.ConnectAsync(ssid, CancellationToken.None, cookieHeader: cookies);
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

            if (args.Any(a => a.Equals("--rsi", StringComparison.OrdinalIgnoreCase)))
                await ReportRsiParityAsync(session, symbol);

            if (args.Any(a => a.Equals("--candles", StringComparison.OrdinalIgnoreCase)))
                await DumpCandlesAsync(session, symbol);

            if (args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
                await ProbeIndicatorFeedAsync(session);

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

    /// <summary>
    /// Prints everything needed to check the bot's RSI against the Binolla chart.
    ///
    /// Compare "RSI(14) closed" below with the RSI the platform draws on the same pair
    /// at the same minute. They should now agree to ~0.1. The drift table shows why the
    /// old constant offset could never work: the same last bar yields a different RSI
    /// depending on how much history you feed it.
    /// </summary>
    private static async Task ReportRsiParityAsync(BinollaSession session, string symbol)
    {
        var now = DateTimeOffset.UtcNow;
        Console.WriteLine();
        Console.WriteLine($"=== RSI parity for {symbol} @ {now:HH:mm:ss} UTC ===");

        var oneMin = await LoadClosedAsync(session, symbol, 60, now);
        Console.WriteLine($"1m closed+contiguous : {oneMin.Count}  (need {IndicatorWarmup.ForRsi(14)} for chart parity)");

        if (oneMin.Count < 15)
        {
            Console.WriteLine("Not enough 1m data to compute RSI.");
            return;
        }

        var closes = oneMin.Select(c => c.Close).ToList();
        var last = oneMin[^1];
        Console.WriteLine($"last closed bar      : {last.Timestamp:HH:mm} -> {last.Timestamp.AddMinutes(1):HH:mm}  close={last.Close}");
        Console.WriteLine($"RSI(14) closed       : {Indicators.Rsi(closes, 14):F2}   <-- compare with the platform");

        Console.WriteLine();
        Console.WriteLine("  drift by history depth (same last bar, different start):");
        foreach (var depth in new[] { 20, 30, 50, 100, 150, 200, 300 })
        {
            if (closes.Count < depth) continue;
            var slice = closes.Skip(closes.Count - depth).ToList();
            Console.WriteLine($"    last {depth,3} bars -> RSI {Indicators.Rsi(slice, 14):F2}");
        }

        Console.WriteLine();
        var emaFast = Indicators.Ema(closes, 9);
        var emaSlow = Indicators.Ema(closes, 21);
        Console.WriteLine($"EMA9 / EMA21         : {emaFast:F5} / {emaSlow:F5}");

        var trend = await LoadClosedAsync(session, symbol, 900, now);
        var needed = IndicatorWarmup.ForEma(200);
        Console.WriteLine($"15m closed+contiguous: {trend.Count}  (need {needed} for the EMA200 trend filter)");
        Console.WriteLine(trend.Count >= needed
            ? $"  EMA200(15m)        : {Indicators.Ema(trend.Select(c => c.Close).ToList(), 200):F5}  -> trend filter USABLE"
            : "  -> NOT enough 15m history: set Strategy:EmaUseTrendFilter=false or the EMA strategy will skip every cross.");
    }

    /// <summary>
    /// Prints the last closed candles exactly as the bot sees them, with the running RSI.
    ///
    /// <para>This is the settling check: put it side by side with the platform chart. If
    /// every row matches, the bot and the chart are reading the same market and any
    /// remaining disagreement is an indicator setting, not a data bug. If a row differs,
    /// that row names the exact minute and field to chase — no more guessing.</para>
    /// </summary>
    private static async Task DumpCandlesAsync(BinollaSession session, string symbol, int count = 15)
    {
        var now = DateTimeOffset.UtcNow;
        var bars = await LoadClosedAsync(session, symbol, 60, now);
        if (bars.Count == 0)
        {
            Console.WriteLine("No closed candles.");
            return;
        }

        var closes = bars.Select(c => c.Close).ToList();
        var rsi = Indicators.RsiSeries(closes, 14);

        Console.WriteLine();
        Console.WriteLine($"=== last {count} CLOSED 1m candles for {symbol} (UTC) ===");
        Console.WriteLine("  time         open      high      low       close     RSI14");
        Console.WriteLine("  " + new string('-', 62));

        var from = Math.Max(0, bars.Count - count);
        for (var i = from; i < bars.Count; i++)
        {
            var b = bars[i];
            static string F(decimal? v) => v is null ? "    -   " : v.Value.ToString("F5");
            Console.WriteLine(
                $"  {b.Timestamp:HH:mm}  {F(b.Open),9} {F(b.High),9} {F(b.Low),9} {F(b.Close),9}  {rsi[i],7:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("  Compare row by row with the chart. A mismatch names the exact minute to chase.");
    }

    /// <summary>
    /// Answers one question with data instead of assumption: does Binolla ever send
    /// indicator VALUES on the socket, so the bot could read RSI instead of computing it?
    ///
    /// <para>Sends the commands the web client uses to restore a chart and prints every
    /// event the router does not already handle. If an <c>s_indicator</c> reply arrives
    /// carrying numbers per candle, reading RSI off the wire is on the table. If it only
    /// carries which indicators are switched on (the usual design — the browser computes
    /// the values), then computing locally is the only option.</para>
    /// </summary>
    private static async Task ProbeIndicatorFeedAsync(BinollaSession session)
    {
        Console.WriteLine();
        Console.WriteLine("=== probing for a server-side indicator feed ===");

        foreach (var frame in new[]
                 {
                     "42[\"indicator/list\"]",
                     "42[\"drawing/load\"]",
                     "42[\"settings/list\"]",
                 })
        {
            var sent = await session.SendDiagnosticFrameAsync(frame);
            Console.WriteLine($"  sent {frame}  -> {(sent ? "ok" : "socket not connected")}");
        }

        // Replies are asynchronous; give them a moment to land in the log.
        await Task.Delay(TimeSpan.FromSeconds(5));

        Console.WriteLine();
        Console.WriteLine("  Now check what came back:");
        Console.WriteLine("    pm2 logs scaralpha-api | grep unknown_event");
        Console.WriteLine("  or re-run this tool without 2>/dev/null and look for 'unknown_event'.");
    }

    /// <summary>Fetches one timeframe and reduces it to the gap-free closed series the bot uses.</summary>
    private static async Task<IReadOnlyList<RsiCandle>> LoadClosedAsync(
        BinollaSession session,
        string symbol,
        int period,
        DateTimeOffset now)
    {
        try
        {
            var history = await session.GetHistoryAsync(symbol, period);
            List<CandlestickData> raw;
            lock (history.Candles)
                raw = history.Candles.ToList();

            var span = TimeSpan.FromSeconds(period);
            var candles = MinuteBars.Normalize(raw, period)
                .Select(c =>
                {
                    var start = DateTimeOffset.FromUnixTimeSeconds(
                        MinuteBars.BucketStartUnix(c.Timestamp, period));
                    return new RsiCandle(start, (decimal)c.Close, start + span);
                })
                .ToList();

            var prepared = CandleSeries.Prepare(candles, period, now);
            if (prepared.GapsDropped > 0)
                Console.WriteLine($"  ({period}s) dropped {prepared.GapsDropped} bars before a time gap");
            return prepared.Closed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ({period}s) history unavailable: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<RsiCandle>();
        }
    }
}
