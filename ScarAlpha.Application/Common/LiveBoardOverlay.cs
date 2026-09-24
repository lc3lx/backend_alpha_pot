using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Application.Common;

/// <summary>
/// Forming-bar RSI for the monitor board: closed series plus the latest tick as the
/// in-progress candle. Closed RSI stays on the completed bar; this number is display-only.
/// </summary>
public static class LiveBoardOverlay
{
    public static decimal Rsi(IReadOnlyList<decimal> closedCloses, decimal? livePrice, int period = 14)
    {
        var closedRsi = ClosedRsi(closedCloses, period);
        if (livePrice is not decimal price || price <= 0m || closedCloses.Count < period)
            return closedRsi;

        var liveCloses = closedCloses.Concat(new[] { price }).ToList();
        var computed = Indicators.Rsi(liveCloses, period);
        return computed is decimal value
            ? Math.Round(value, 2, MidpointRounding.AwayFromZero)
            : closedRsi;
    }

    private static decimal ClosedRsi(IReadOnlyList<decimal> closedCloses, int period)
    {
        if (closedCloses.Count < period + 1)
            return 0m;
        var computed = Indicators.Rsi(closedCloses, period);
        return computed is decimal value
            ? Math.Round(value, 2, MidpointRounding.AwayFromZero)
            : 0m;
    }
}
