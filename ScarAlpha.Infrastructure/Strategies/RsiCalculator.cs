using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.Strategies;

/// <summary>
/// Deterministic Wilder RSI calculator.
/// </summary>
public sealed class RsiCalculator : IRsiCalculator
{
    public decimal CalculateRsi(IReadOnlyList<decimal> closes, RsiStrategyOptions options)
    {
        if (closes is null) throw new ArgumentNullException(nameof(closes));
        if (options.Period is <= 0) throw new ArgumentOutOfRangeException(nameof(options.Period));
        if (closes.Count < options.Period + 1)
            throw new InvalidOperationException("Insufficient candle closes for RSI calculation.");

        // Initial averages based on the first `Period` deltas.
        decimal gainSum = 0m;
        decimal lossSum = 0m;
        for (var i = 1; i <= options.Period; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta > 0) gainSum += delta;
            else lossSum += -delta;
        }

        var avgGain = gainSum / options.Period;
        var avgLoss = lossSum / options.Period;

        // Wilder smoothing over subsequent deltas.
        for (var i = options.Period + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var gain = delta > 0 ? delta : 0m;
            var loss = delta < 0 ? -delta : 0m;

            avgGain = (avgGain * (options.Period - 1) + gain) / options.Period;
            avgLoss = (avgLoss * (options.Period - 1) + loss) / options.Period;
        }

        if (avgLoss == 0m)
        {
            // No losses => RSI is max unless also flat (no gains/losses).
            if (avgGain == 0m) return 50m;
            return 100m;
        }

        var rs = avgGain / avgLoss;
        var rsi = 100m - (100m / (1m + rs));
        return rsi;
    }
}

