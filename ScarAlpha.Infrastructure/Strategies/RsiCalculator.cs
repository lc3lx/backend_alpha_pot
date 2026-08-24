using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.Strategies;

/// <summary>
/// Wilder RSI for the signal path. The maths lives in <see cref="Indicators"/> so every
/// caller — signals, backtest, display — sees one value, including any broker calibration.
/// </summary>
public sealed class RsiCalculator : IRsiCalculator
{
    public decimal CalculateRsi(IReadOnlyList<decimal> closes, RsiStrategyOptions options)
    {
        if (closes is null) throw new ArgumentNullException(nameof(closes));
        if (options.Period is <= 0) throw new ArgumentOutOfRangeException(nameof(options.Period));
        if (closes.Count < options.Period + 1)
            throw new InvalidOperationException("Insufficient candle closes for RSI calculation.");

        // Delegates to Indicators rather than repeating Wilder here. A second copy meant
        // the broker calibration applied to some code paths and not to the one that
        // actually produces signals — the displayed value and the entry decision have to
        // come from the same place.
        return Indicators.RsiSeries(closes, options.Period)[^1];
    }
}

