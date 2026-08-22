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

    /// <summary>
    /// Master switch for regime filtering (appsettings <c>Strategy:Regime:Enabled</c>).
    /// Off means the gate behaves exactly as it did before regime existed.
    /// </summary>
    public static bool RegimeEnabled { get; set; }

    public static bool TryValidateForTrade(
        StrategySignal signal,
        DateTimeOffset now,
        out string? rejectCode) =>
        TryValidateForTrade(signal, now, botStrategyId: null, regime: null, out rejectCode);

    /// <summary>
    /// Full gate, including the regime veto.
    ///
    /// <para>The regime check lives HERE rather than at execution time on purpose:
    /// <c>BotSignalWorker</c> marks the user held for a whole trade duration as soon as
    /// this gate passes. Rejecting later would freeze the bot on a setup that never
    /// became a trade.</para>
    /// </summary>
    public static bool TryValidateForTrade(
        StrategySignal signal,
        DateTimeOffset now,
        string? botStrategyId,
        RegimeSnapshot? regime,
        out string? rejectCode)
    {
        if (RegimeEnabled)
        {
            var snapshot = regime ?? (signal.RegimeApplied
                ? new RegimeSnapshot(
                    signal.Regime, 0m, 0m, 0m, 0m, 0m, 0m,
                    signal.RelativeVolume,
                    VolumeAvailable: signal.RelativeVolume is not null,
                    VolumeOk: signal.VolumeOk,
                    Reason: signal.RegimeReason)
                : null);

            if (snapshot is not null &&
                !RegimeRouting.IsAllowed(botStrategyId, signal.StrategyId, signal.Signal, snapshot, out rejectCode))
            {
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-REGIME",
                    "StrategyGate.TryValidateForTrade",
                    "regime_reject",
                    new
                    {
                        asset = signal.Asset,
                        side = signal.Signal,
                        engine = signal.StrategyId,
                        bot = botStrategyId,
                        regime = snapshot.Regime.ToString(),
                        reason = snapshot.Reason,
                        rejectCode
                    },
                    runId: "regime");
                // #endregion
                return false;
            }
        }

        // RSI keeps its own gate: the entry levels and the zone backtest are
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
