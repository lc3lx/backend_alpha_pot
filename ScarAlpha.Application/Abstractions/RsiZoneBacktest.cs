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
    public const int LookbackCandles = 200;

    public static (RsiBacktestStats Call, RsiBacktestStats Put) Evaluate(
        IReadOnlyList<decimal> closedCloses,
        RsiStrategyOptions options)
    {
        var lookback = LookbackCandles;
        var oversold = RsiEntryLevels.CallMax;
        var overbought = RsiEntryLevels.PutMin;
        var currentIndex = closedCloses.Count - 1;
        var firstIndex = Math.Max(options.Period, currentIndex - lookback);
        var lastIndex = currentIndex - 1;
        var rsiSeries = BuildRsiSeries(closedCloses, options);

        var callTotal = 0;
        var callWins = 0;
        var putTotal = 0;
        var putWins = 0;
        var inCallZone = false;
        var inPutZone = false;
        var callTouchIndex = -1;
        var putTouchIndex = -1;

        for (var i = firstIndex; i <= lastIndex; i++)
        {
            var historicalRsi = rsiSeries[i];

            if (historicalRsi <= oversold)
            {
                if (!inCallZone)
                {
                    inCallZone = true;
                    callTouchIndex = i;
                }
            }
            else if (inCallZone)
            {
                callTotal++;
                // Left 25: respected if price bounced up from the touch.
                if (closedCloses[i] > closedCloses[callTouchIndex])
                    callWins++;
                inCallZone = false;
                callTouchIndex = -1;
            }

            if (historicalRsi >= overbought)
            {
                if (!inPutZone)
                {
                    inPutZone = true;
                    putTouchIndex = i;
                }
            }
            else if (inPutZone)
            {
                putTotal++;
                // Left 75: respected if price dropped from the touch.
                if (closedCloses[i] < closedCloses[putTouchIndex])
                    putWins++;
                inPutZone = false;
                putTouchIndex = -1;
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
            ExpiryCandles: options.ExpiryCandles,
            MinimumSuccessRate: minimum,
            Passed: total > 0 && successRate >= minimum);
    }
}
