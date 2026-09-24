namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// 200×1-minute zone-respect backtest. Always computed on every closed bar — Call
/// and Put in the same pass — so both sides are already in cache when RSI later
/// closes at 25 or 75. Entry still waits for that closed touch; the numbers do not.
///
/// Call visit: RSI touched ≤ 25, then left the zone, and price was higher after
/// (zone respected — bounced up).
/// Put visit: RSI touched ≥ 75, then left the zone, and price was lower after
/// (zone respected — dropped).
/// Passed when visits ≥ <see cref="RsiEntryLevels.MinZoneVisits"/> and success ≥ 75%.
/// </summary>
public static class RsiZoneBacktest
{
    public const int LookbackCandles = 200;

    /// <summary>
    /// The side the UI/worker should look at for this closed RSI: Call at/below 25,
    /// Put at/above 75, otherwise the nearer side so the rate is visible while waiting.
    /// </summary>
    public static RsiBacktestStats? DisplayFor(
        decimal closedRsi,
        RsiBacktestStats? call,
        RsiBacktestStats? put)
    {
        var preferred = RsiEntryLevels.IsCallRsi(closedRsi)
            ? call
            : RsiEntryLevels.IsPutRsi(closedRsi)
                ? put
                : closedRsi >= 50m ? put : call;
        // A side with no zone visits is an empty result, not a missing backtest.
        // Show the other side so the board still has the rate that was computed.
        if (HasSamples(preferred)) return preferred;
        var other = ReferenceEquals(preferred, call) ? put : call;
        if (HasSamples(other)) return other;
        return preferred ?? other;
    }

    private static bool HasSamples(RsiBacktestStats? stats) =>
        stats is { TotalSignals: > 0 };

    public static (RsiBacktestStats Call, RsiBacktestStats Put) Evaluate(
        IReadOnlyList<decimal> closedCloses,
        RsiStrategyOptions options)
    {
        var lookback = LookbackCandles;
        var oversold = options.Oversold > 0 ? options.Oversold : RsiEntryLevels.CallMax;
        var overbought = options.Overbought > 0 ? options.Overbought : RsiEntryLevels.PutMin;
        var expiry = options.ExpiryCandles is >= 3 and <= 5 ? options.ExpiryCandles : 5;

        var currentIndex = closedCloses.Count - 1;
        var firstIndex = Math.Max(options.Period, currentIndex - lookback);
        var lastIndex = currentIndex - expiry; // No lookahead: only evaluate trades that have reached expiry
        var rsiSeries = BuildRsiSeries(closedCloses, options);

        var callTotal = 0;
        var callWins = 0;
        var putTotal = 0;
        var putWins = 0;

        if (lastIndex >= firstIndex)
        {
            for (var i = firstIndex; i <= lastIndex; i++)
            {
                var historicalRsi = rsiSeries[i];

                if (historicalRsi <= oversold)
                {
                    callTotal++;
                    // CALL Win: price after expiry is higher than signal candle close
                    if (closedCloses[i + expiry] > closedCloses[i])
                        callWins++;
                }

                if (historicalRsi >= overbought)
                {
                    putTotal++;
                    // PUT Win: price after expiry is lower than signal candle close
                    if (closedCloses[i + expiry] < closedCloses[i])
                        putWins++;
                }
            }
        }

        return (ToStats(callTotal, callWins, options, lookback), ToStats(putTotal, putWins, options, lookback));
    }

    /// <summary>
    /// Same series the signals and the UI use — including calibration, so a zone visit
    /// here means the same thing as an entry level there.
    /// </summary>
    private static decimal[] BuildRsiSeries(IReadOnlyList<decimal> closes, RsiStrategyOptions options) =>
        Indicators.RsiSeries(closes, options.Period);

    private static RsiBacktestStats ToStats(int total, int wins, RsiStrategyOptions options, int lookback)
    {
        var failed = total - wins;
        var successRate = total == 0
            ? 0m
            : Math.Round(wins * 100m / total, 2, MidpointRounding.AwayFromZero);
        var minimum = Math.Max(options.MinimumSuccessRate, RsiEntryLevels.MinSuccessRate);
        var visitsOk = total >= RsiEntryLevels.MinZoneVisits;
        return new RsiBacktestStats(
            TotalSignals: total,
            SuccessfulSignals: wins,
            FailedSignals: failed,
            SuccessRate: successRate,
            LookbackCandles: lookback,
            ExpiryCandles: options.ExpiryCandles is >= 3 and <= 5 ? options.ExpiryCandles : 5,
            MinimumSuccessRate: minimum,
            Passed: visitsOk && successRate >= minimum);
    }
}
