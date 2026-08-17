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
    /// Max seconds after the last CLOSED bar to still allow Call/Put when the
    /// feed has no forming candle. Live RSI at 25/75 enters immediately and
    /// ignores this lag. Default is <see cref="RsiEntryLevels.SetupTtlSeconds"/>.
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
/// Entry order (strict):
/// 1) Live RSI of the pair is primary — Put only if ≥ 75, Call only if ≤ 25.
/// 2) Backtest runs in parallel and is ready, but has ZERO entry value until step 1 is true.
/// 3) Only after RSI is at the extreme, require matching backtest Passed (≥ 75%, visits &gt; 0).
/// 4) Setup must still be within <see cref="SetupTtlSeconds"/> of first sight.
/// </summary>
public static class RsiEntryLevels
{
    public const decimal CallMax = 25m;
    public const decimal PutMin = 75m;
    public const decimal MinSuccessRate = 75m;
    /// <summary>Put side: 80 → 74. Call side below 26: 20 → 26.</summary>
    public const decimal BinollaAlignDelta = 6m;
    public const decimal BinollaCallBoostBelow = 26m;
    /// <summary>Enter on the first tick both conditions are true. After this, look for a new touch.</summary>
    public const int SetupTtlSeconds = 5;

    public static bool IsCallRsi(decimal rsi) => rsi <= CallMax;
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

    /// <summary>RSI first; backtest only consulted when live RSI is already at the extreme.</summary>
    public static bool CanEnterCall(decimal rsi, RsiBacktestStats? backtest) =>
        IsCallRsi(rsi) && BacktestOk(backtest);

    /// <summary>RSI first; backtest only consulted when live RSI is already at the extreme.</summary>
    public static bool CanEnterPut(decimal rsi, RsiBacktestStats? backtest) =>
        IsPutRsi(rsi) && BacktestOk(backtest);

    /// <summary>
    /// Final gate before placing a bot trade. Live RSI is checked before backtest.
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

        if (signal.LiveRsi is not decimal liveRsi)
        {
            rejectCode = "LIVE_RSI_REQUIRED";
            return false;
        }

        // Primary gate: live RSI. Until this passes, backtest is irrelevant.
        if (signal.Signal == "Call")
        {
            if (!IsCallRsi(liveRsi))
            {
                rejectCode = "RSI_NOT_OVERSOLD";
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-A",
                    "RsiEntryLevels.TryValidateForTrade",
                    "gate_reject_rsi",
                    new { signal = signal.Signal, liveRsi, closedRsi = signal.Rsi, rejectCode, callMax = CallMax, putMin = PutMin },
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
            if (!IsPutRsi(liveRsi))
            {
                rejectCode = "RSI_NOT_OVERBOUGHT";
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-A",
                    "RsiEntryLevels.TryValidateForTrade",
                    "gate_reject_rsi",
                    new { signal = signal.Signal, liveRsi, closedRsi = signal.Rsi, rejectCode, callMax = CallMax, putMin = PutMin },
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

        var ageSeconds = (now - signal.CandleTime).TotalSeconds;
        if (ageSeconds < 0)
            ageSeconds = 0;
        if (ageSeconds > SetupTtlSeconds)
        {
            rejectCode = "SETUP_EXPIRED";
            return false;
        }

        var violation = (signal.Signal == "Put" && liveRsi < PutMin) ||
                        (signal.Signal == "Call" && liveRsi > CallMax);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-A",
            "RsiEntryLevels.TryValidateForTrade",
            "gate_pass",
            new
            {
                signal = signal.Signal,
                liveRsi,
                closedRsi = signal.Rsi,
                rsiEqClosed = liveRsi == signal.Rsi,
                putOk = liveRsi >= PutMin,
                callOk = liveRsi <= CallMax,
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
    /// Computes live RSI and a 200×1m zone-respect backtest on every snapshot.
    /// Call = live RSI ≤ 25 first, then call backtest must pass.
    /// Put  = live RSI ≥ 75 first, then put backtest must pass.
    /// Backtest runs in parallel but has no entry value until live RSI is at the extreme.
    /// Emits on the first moment both conditions are true and expires after 5 seconds
    /// — does not wait for the forming candle to close or for the next minute.
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

