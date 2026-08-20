using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Application.Abstractions;

public enum RsiSignalType
{
    Call,
    Put,
    None
}

public sealed record RsiStrategyOptions(
    int Period,
    decimal Oversold,
    decimal Overbought,
    int TimeframeSeconds,
    int BacktestCandleCount = 200,
    int ExpiryCandles = 5,
    decimal MinimumSuccessRate = 75m,
    /// <summary>
    /// Max seconds after the last CLOSED bar's close time to still allow Call/Put.
    /// Entry uses closed RSI only (not the forming bar). Default is
    /// <see cref="RsiEntryLevels.SetupTtlSeconds"/>.
    /// </summary>
    int MaxEntryLagSeconds = 5)
{
    public static RsiStrategyOptions Default60Seconds =>
        new(
            Period: 14,
            Oversold: RsiEntryLevels.CallMax,
            Overbought: RsiEntryLevels.PutMin,
            TimeframeSeconds: 60,
            BacktestCandleCount: RsiZoneBacktest.LookbackCandles);

    /// <summary>Maps bot trade duration (180/240/300) to expiry candles 3–5 (default 5).</summary>
    public static RsiStrategyOptions FromBotDurationSeconds(int durationSeconds)
    {
        var candles = durationSeconds / 60;
        if (candles is < 3 or > 5)
            candles = 5;
        return Default60Seconds with { ExpiryCandles = candles };
    }
}

/// <summary>
/// Entry order (strict) — candle-close confirmation:
/// 1) Wait for the 1m candle that touched 25/75 to CLOSE.
/// 2) Put only if closed RSI stayed above 75; Call only if closed RSI stayed below 25.
///    If it closed back under 75 / above 25 → no entry.
/// 3) Backtest has ZERO entry value until step 2 is true, then must Pass (≥ 75%).
/// 4) Setup must still be within <see cref="SetupTtlSeconds"/> of that candle's close.
/// </summary>
public static class RsiEntryLevels
{
    public const decimal CallMax = 25m;
    public const decimal PutMin = 75m;
    public const decimal MinSuccessRate = 75m;
    /// <summary>Put side: 80 → 74. Call side below 26: 20 → 26.</summary>
    public const decimal BinollaAlignDelta = 6m;
    public const decimal BinollaCallBoostBelow = 26m;
    /// <summary>Enter in the first seconds after the extreme candle closes. Then wait for a new closed touch.</summary>
    public const int SetupTtlSeconds = 5;

    /// <summary>Call: candle closed at/below 25 (still under the level).</summary>
    public static bool IsCallRsi(decimal rsi) => rsi <= CallMax;
    /// <summary>Put: candle closed at/above 75 (still above the level).</summary>
    public static bool IsPutRsi(decimal rsi) => rsi >= PutMin;

    public static decimal AlignToBinolla(decimal rsi)
    {
        var shifted = rsi < BinollaCallBoostBelow
            ? rsi + BinollaAlignDelta
            : rsi - BinollaAlignDelta;
        if (shifted < 0m) return 0m;
        if (shifted > 100m) return 100m;
        return shifted;
    }

    public static bool BacktestOk(RsiBacktestStats? backtest) =>
        backtest is { Passed: true, TotalSignals: > 0 } &&
        backtest.SuccessRate >= MinSuccessRate &&
        backtest.SuccessRate >= backtest.MinimumSuccessRate;

    /// <summary>Closed RSI first; backtest only consulted when the closed bar is still at the extreme.</summary>
    public static bool CanEnterCall(decimal closedRsi, RsiBacktestStats? backtest) =>
        IsCallRsi(closedRsi) && BacktestOk(backtest);

    /// <summary>Closed RSI first; backtest only consulted when the closed bar is still at the extreme.</summary>
    public static bool CanEnterPut(decimal closedRsi, RsiBacktestStats? backtest) =>
        IsPutRsi(closedRsi) && BacktestOk(backtest);

    /// <summary>
    /// Final gate before placing a bot trade. Closed RSI is checked before backtest.
    /// </summary>
    public static bool TryValidateForTrade(
        StrategySignal signal,
        DateTimeOffset now,
        out string? rejectCode)
    {
        rejectCode = null;

        if (!string.IsNullOrEmpty(signal.AutomationError) &&
            signal.AutomationError is "SETUP_EXPIRED" or "SETUP_CONSUMED" or "SIGNAL_STALE")
        {
            rejectCode = signal.AutomationError;
            return false;
        }

        // Primary gate: closed candle RSI (signal.Rsi). Forming/live RSI does not enter.
        var closedRsi = signal.Rsi;
        if (signal.Signal == "Call")
        {
            if (!IsCallRsi(closedRsi))
            {
                rejectCode = "RSI_NOT_OVERSOLD";
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-A",
                    "RsiEntryLevels.TryValidateForTrade",
                    "gate_reject_rsi",
                    new { signal = signal.Signal, liveRsi = signal.LiveRsi, closedRsi, rejectCode, callMax = CallMax, putMin = PutMin },
                    runId: "rsi-zone");
                // #endregion
                return false;
            }

            if (!BacktestOk(signal.Backtest))
            {
                rejectCode = "BACKTEST_NOT_PASSED";
                return false;
            }
        }
        else if (signal.Signal == "Put")
        {
            if (!IsPutRsi(closedRsi))
            {
                rejectCode = "RSI_NOT_OVERBOUGHT";
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-A",
                    "RsiEntryLevels.TryValidateForTrade",
                    "gate_reject_rsi",
                    new { signal = signal.Signal, liveRsi = signal.LiveRsi, closedRsi, rejectCode, callMax = CallMax, putMin = PutMin },
                    runId: "rsi-zone");
                // #endregion
                return false;
            }

            if (!BacktestOk(signal.Backtest))
            {
                rejectCode = "BACKTEST_NOT_PASSED";
                return false;
            }
        }
        else
        {
            rejectCode = "NO_SIGNAL";
            return false;
        }

        // CandleTime is the closed bar's close instant.
        var ageSeconds = (now - signal.CandleTime).TotalSeconds;
        if (ageSeconds < 0)
            ageSeconds = 0;
        if (ageSeconds > SetupTtlSeconds)
        {
            rejectCode = "SETUP_EXPIRED";
            return false;
        }

        var violation = (signal.Signal == "Put" && closedRsi < PutMin) ||
                        (signal.Signal == "Call" && closedRsi > CallMax);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-A",
            "RsiEntryLevels.TryValidateForTrade",
            "gate_pass",
            new
            {
                signal = signal.Signal,
                liveRsi = signal.LiveRsi,
                closedRsi,
                putOk = closedRsi >= PutMin,
                callOk = closedRsi <= CallMax,
                violation,
                ageSec = Math.Round(ageSeconds, 2)
            },
            runId: "rsi-zone");
        // #endregion
        return true;
    }
}

public sealed record RsiCandle(
    DateTimeOffset Timestamp,
    decimal Close,
    DateTimeOffset? EndTimestamp);

public sealed record RsiBacktestStats(
    int TotalSignals,
    int SuccessfulSignals,
    int FailedSignals,
    decimal SuccessRate,
    int LookbackCandles,
    int ExpiryCandles,
    decimal MinimumSuccessRate,
    bool Passed);

/// <summary>Server-computed, closed-candle RSI strategy signal snapshot.</summary>
public sealed record StrategySignal(
    string StrategyId,
    string Asset,
    string Signal,
    decimal Rsi,
    DateTimeOffset CandleTime,
    string Timeframe,
    RsiBacktestStats? Backtest = null,
    string? AutomatedTradeId = null,
    string? AutomationError = null,
    decimal? LiveRsi = null);

public interface IRsiCalculator
{
    /// <summary>
    /// Calculates the latest RSI value from the provided candle closes.
    /// Uses Wilder's RSI (deterministic, no randomness).
    /// </summary>
    decimal CalculateRsi(IReadOnlyList<decimal> closes, RsiStrategyOptions options);
}

public interface IRsiSignalService
{
    /// <summary>
    /// Computes closed + live RSI and a 200×1m zone-respect backtest on every snapshot.
    /// Call = last CLOSED bar RSI ≤ 25, then call backtest must pass.
    /// Put  = last CLOSED bar RSI ≥ 75, then put backtest must pass.
    /// Forming-bar (live) RSI is display-only — a wick to 75/25 that closes back inside does not enter.
    /// Emits in the first <see cref="RsiEntryLevels.SetupTtlSeconds"/> after that candle closes.
    /// Anti-repeat is checked but not recorded here — call
    /// <see cref="MarkSignalEmitted"/> only after a successful trade place.
    /// </summary>
    Task<StrategySignal> GetSignalAsync(
        Guid userId,
        string asset,
        IReadOnlyList<RsiCandle> candles,
        RsiStrategyOptions options,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Records that a Call/Put for this candle was consumed (trade placed),
    /// so subsequent polls return None for the same closed candle.
    /// </summary>
    void MarkSignalEmitted(
        Guid userId,
        string asset,
        int timeframeSeconds,
        DateTimeOffset candleTime);
}

