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
    int BacktestCandleCount = 400,
    int ExpiryCandles = 5,
    decimal MinimumSuccessRate = 75m)
{
    public static RsiStrategyOptions Default60Seconds =>
        new(Period: 14, Oversold: 25m, Overbought: 75m, TimeframeSeconds: 60);
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
    string? AutomationError = null);

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
    /// Computes an RSI signal on the latest closed 1-minute candle only.
    /// A CALL requires RSI <= Oversold and a PUT requires RSI >= Overbought.
    /// The current signal is emitted only after the same-direction historical
    /// backtest meets the configured success rate; no open-candle data is used.
    /// </summary>
    Task<StrategySignal> GetSignalAsync(
        Guid userId,
        string asset,
        IReadOnlyList<RsiCandle> candles,
        RsiStrategyOptions options,
        DateTimeOffset now,
        CancellationToken ct = default);
}

