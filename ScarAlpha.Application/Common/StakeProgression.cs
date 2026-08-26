namespace ScarAlpha.Application.Common;

/// <summary>
/// Computes the next bot trade size after a win/loss according to the selected technical indicator (stake mode).
/// </summary>
public static class StakeProgression
{
    public const string RedSignalPro = "red-signal-pro";
    public const string AlphaMomentum = "alpha-momentum";
    public const string ScarPrecision = "scar-precision";
    public const string TrendBreaker = "trend-breaker";

    public static string NormalizeMode(string? stakeMode)
    {
        var id = stakeMode?.Trim().ToLowerInvariant();
        return id switch
        {
            AlphaMomentum => AlphaMomentum,
            ScarPrecision => ScarPrecision,
            TrendBreaker => TrendBreaker,
            _ => RedSignalPro,
        };
    }

    /// <param name="baseAmount">Initial stake configured when the bot started or amount was last reset.</param>
    /// <param name="lastTradeAmount">Stake used on the trade that just lost.</param>
    public static decimal CalculateNextAfterLoss(string? stakeMode, decimal baseAmount, decimal lastTradeAmount)
    {
        var last = lastTradeAmount > 0 ? lastTradeAmount : baseAmount;
        var next = NormalizeMode(stakeMode) switch
        {
            RedSignalPro => baseAmount,
            AlphaMomentum => last * 1.5m,
            ScarPrecision => last * 2m,
            TrendBreaker => last * 3m,
            _ => baseAmount,
        };
        return ClampAndRound(next);
    }

    public static decimal ResetAfterWin(decimal baseAmount) => ClampAndRound(baseAmount);

    private static decimal ClampAndRound(decimal value)
    {
        if (value <= 0) return 0.01m;
        if (value > 100_000m) return 100_000m;
        return decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }
}
