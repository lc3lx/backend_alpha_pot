namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// 200×1-minute zone-respect backtest. Always computed in parallel with live RSI
/// so it is ready the moment RSI touches 25 (Call) or 75 (Put). It has no entry
/// value until that live touch happens.
///
/// Call visit: RSI touched ≤ 25, then left the zone, and price was higher after
/// (zone respected — bounced up).
/// Put visit: RSI touched ≥ 75, then left the zone, and price was lower after
/// (zone respected — dropped).
/// Passed when visits &gt; 0 and success ≥ 75%.
/// </summary>
public static class RsiZoneBacktest
{
    public const int LookbackCandles = 400;

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
        return new RsiBacktestStats(
            TotalSignals: total,
            SuccessfulSignals: wins,
            FailedSignals: failed,
            SuccessRate: successRate,
            LookbackCandles: lookback,
            ExpiryCandles: options.ExpiryCandles is >= 3 and <= 5 ? options.ExpiryCandles : 5,
            MinimumSuccessRate: minimum,
            Passed: total > 0 && successRate >= minimum);
    }
}
