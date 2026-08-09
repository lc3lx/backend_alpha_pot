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
    int TimeframeSeconds)
{
    public static RsiStrategyOptions Default60Seconds =>
        new(Period: 14, Oversold: 30m, Overbought: 70m, TimeframeSeconds: 60);
}

public sealed record RsiCandle(
    DateTimeOffset Timestamp,
    decimal Close,
    DateTimeOffset? EndTimestamp);

/// <summary>
/// Server-computed strategy signal snapshot (no automatic trading in Phase 5).
/// </summary>
public sealed record StrategySignal(
    string StrategyId,
    string Asset,
    string Signal,
    decimal Rsi,
    DateTimeOffset CandleTime,
    string Timeframe);

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
    /// Computes RSI crossing signal for the latest closed candle.
    /// - Closed candles only (based on EndTimestamp when available)
    /// - Crossing logic: oversold(<=30) to above(>30) => CALL, overbought(>=70) to below(<70) => PUT
    /// - No repeat signal for the same candleTime
    /// </summary>
    Task<StrategySignal> GetSignalAsync(
        Guid userId,
        string asset,
        IReadOnlyList<RsiCandle> candles,
        RsiStrategyOptions options,
        DateTimeOffset now,
        CancellationToken ct = default);
}

