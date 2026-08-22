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
/// 1) Wait for the 1m candle that touched the entry level to CLOSE.
/// 2) Put only if closed RSI stayed at/above <see cref="PutMin"/>; Call only if it
///    stayed at/below <see cref="CallMax"/>. Closing back inside the range → no entry.
/// 3) Backtest has ZERO entry value until step 2 is true, then must Pass (≥ 75%).
/// 4) Setup must still be within <see cref="SetupTtlSeconds"/> of that candle's close.
/// </summary>
public static class RsiEntryLevels
{
    private static decimal _callMax = 20m;
    private static decimal _putMin = 80m;
    private static int _minZoneVisits = 2;

    /// <summary>
    /// Call entry ceiling — RSI must close at or below this. Configurable
    /// (<c>Strategy:RsiCallMax</c>) because tightening the levels is the change most
    /// likely to need rolling back under live observation.
    /// </summary>
    public static decimal CallMax
    {
        get => _callMax;
        set => _callMax = value is >= 5m and <= 45m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Call level must be between 5 and 45.");
    }

    /// <summary>Put entry floor — RSI must close at or above this (<c>Strategy:RsiPutMin</c>).</summary>
    public static decimal PutMin
    {
        get => _putMin;
        set => _putMin = value is >= 55m and <= 95m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Put level must be between 55 and 95.");
    }

    /// <summary>
    /// Zone visits the backtest must have seen before its success rate means anything.
    ///
    /// <para>At 20/80 a 200-bar window often contains only one visit, and a single lucky
    /// visit scores 100% — which would make the backtest filter stop filtering exactly
    /// when the levels got stricter.</para>
    /// </summary>
    public static int MinZoneVisits
    {
        get => _minZoneVisits;
        set => _minZoneVisits = value is >= 1 and <= 20
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Minimum zone visits must be between 1 and 20.");
    }

    public const decimal MinSuccessRate = 75m;
    /// <summary>Enter in the first seconds after the extreme candle closes. Then wait for a new closed touch.</summary>
    public const int SetupTtlSeconds = 5;

    /// <summary>Call: candle closed at/below the entry level.</summary>
    public static bool IsCallRsi(decimal rsi) => rsi <= CallMax;
    /// <summary>Put: candle closed at/above the entry level.</summary>
    public static bool IsPutRsi(decimal rsi) => rsi >= PutMin;

    public static bool BacktestOk(RsiBacktestStats? backtest) =>
        backtest is { Passed: true } &&
        backtest.TotalSignals >= MinZoneVisits &&
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

/// <summary>
/// One bar as the strategy layer sees it.
///
/// <para>Close is always real. High/Low/Open/Volume are optional because not every bar
/// has them: a bar synthesised from a single live quote has no true range, and Binolla
/// only sends volume on wide enough history rows (never on forming bars). Indicators
/// that need a range must go through <see cref="HasRange"/> rather than assuming.</para>
/// </summary>
public sealed record RsiCandle(
    DateTimeOffset Timestamp,
    decimal Close,
    DateTimeOffset? EndTimestamp,
    decimal? High = null,
    decimal? Low = null,
    decimal? Open = null,
    decimal? Volume = null)
{
    /// <summary>True when this bar carries a real high/low, not just a close.</summary>
    public bool HasRange => High is not null && Low is not null;

    /// <summary>High, or the close when the bar carries no range.</summary>
    public decimal HighOrClose => High ?? Close;

    /// <summary>Low, or the close when the bar carries no range.</summary>
    public decimal LowOrClose => Low ?? Close;
}

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
    decimal? LiveRsi = null,
    /// <summary>
    /// Market regime this signal was produced in. <see cref="MarketRegime.Unclear"/>
    /// when regime detection is off, which is why <see cref="RegimeApplied"/> exists —
    /// "not measured" and "measured as unclear" must not look the same.
    /// </summary>
    MarketRegime Regime = MarketRegime.Unclear,
    string? RegimeReason = null,
    /// <summary>True when the regime filter was actually evaluated for this signal.</summary>
    bool RegimeApplied = false,
    decimal? RelativeVolume = null,
    bool VolumeOk = true);

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

