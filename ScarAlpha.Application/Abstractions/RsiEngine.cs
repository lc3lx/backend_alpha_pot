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
    /// ignores this lag.
    /// </summary>
    int MaxEntryLagSeconds = 20)
{
    public static RsiStrategyOptions Default60Seconds =>
        new(Period: 14, Oversold: 25m, Overbought: 75m, TimeframeSeconds: 60);

    /// <summary>Maps bot trade duration (180/240/300) to expiry candles 3–5 (default 5).</summary>
    public static RsiStrategyOptions FromBotDurationSeconds(int durationSeconds)
    {
        var candles = durationSeconds / 60;
        if (candles is < 3 or > 5)
            candles = 5;
        return Default60Seconds with { ExpiryCandles = candles };
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
    /// Call = live RSI ≤ 25 AND the 200×1m call backtest passed (touch 25, leave, price up).
    /// Put  = live RSI ≥ 75 AND the 200×1m put backtest passed (touch 75, leave, price down).
    /// Does not wait for the forming candle to close.
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

