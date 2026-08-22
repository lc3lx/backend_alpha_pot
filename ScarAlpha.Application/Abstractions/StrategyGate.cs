namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Final check before a bot trade is placed, for whichever strategy produced the signal.
///
/// Each strategy owns its own entry conditions and has already applied them when it
/// built the signal. What every strategy shares — and what is enforced here — is:
///  * the signal is an actual Call/Put,
///  * no automation error is attached,
///  * the closed bar it came from is still inside the entry window.
/// </summary>
public static class StrategyGate
{
    private static readonly string[] BlockingErrors =
        { "SETUP_EXPIRED", "SETUP_CONSUMED", "SIGNAL_STALE" };

    public static bool TryValidateForTrade(
        StrategySignal signal,
        DateTimeOffset now,
        out string? rejectCode)
    {
        // RSI keeps its own gate: the 25/75 levels and the zone backtest are
        // re-checked there rather than trusted from the signal payload.
        if (IsRsi(signal.StrategyId))
            return RsiEntryLevels.TryValidateForTrade(signal, now, out rejectCode);

        rejectCode = null;

        if (!string.IsNullOrEmpty(signal.AutomationError) &&
            Array.IndexOf(BlockingErrors, signal.AutomationError) >= 0)
        {
            rejectCode = signal.AutomationError;
            return false;
        }

        if (signal.Signal is not ("Call" or "Put"))
        {
            rejectCode = "NO_SIGNAL";
            return false;
        }

        var ageSeconds = (now - signal.CandleTime).TotalSeconds;
        if (ageSeconds < 0) ageSeconds = 0;
        if (ageSeconds > RsiEntryLevels.SetupTtlSeconds)
        {
            rejectCode = "SETUP_EXPIRED";
            return false;
        }

        return true;
    }

    private static bool IsRsi(string? strategyId) =>
        string.IsNullOrWhiteSpace(strategyId) ||
        string.Equals(strategyId, "rsi", StringComparison.OrdinalIgnoreCase);
}
