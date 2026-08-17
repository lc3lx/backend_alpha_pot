using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Strategies;

public sealed class RsiSignalService : IRsiSignalService
{
    private readonly IRsiCalculator _calculator;

    // Tracks last consumed (traded) signal candleTime per user+asset+timeframe.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEmittedSignalCandleTime = new();

    public RsiSignalService(IRsiCalculator calculator)
    {
        _calculator = calculator;
    }

    public Task<StrategySignal> GetSignalAsync(
        Guid userId,
        string asset,
        IReadOnlyList<RsiCandle> candles,
        RsiStrategyOptions options,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (candles is null)
            throw new ArgumentNullException(nameof(candles));

        ValidateOptions(options);

        // Closed only when EndTimestamp is known and not in the future — never treat null as closed.
        var closed = candles
            .Where(c => c.EndTimestamp is DateTimeOffset end && end <= now)
            .OrderBy(c => c.Timestamp)
            .ToList();

        // One current candle, a completed expiry window for at least one
        // historical entry, and enough closes for Wilder RSI.
        if (closed.Count < options.Period + options.ExpiryCandles + 2)
            throw new ApiException(ApiErrorCodes.ValidationError, "Insufficient closed candles for RSI backtest.");

        var currentCandle = closed[^1];
        var closes = closed.Select(c => c.Close).ToList();
        var currentRsi = _calculator.CalculateRsi(closes, options);

        // The candle must CLOSE inside the relevant RSI extreme. Touching the
        // level intra-candle is never visible here because open candles were
        // removed above.
        var signalType = currentRsi <= options.Oversold ? RsiSignalType.Call :
            currentRsi >= options.Overbought ? RsiSignalType.Put :
            RsiSignalType.None;

        var roundedRsi = Math.Round(currentRsi, 2, MidpointRounding.AwayFromZero);
        var timeframe = options.TimeframeSeconds.ToString();

        if (signalType == RsiSignalType.None)
        {
            return Task.FromResult(new StrategySignal(
                StrategyId: "rsi",
                Asset: asset.Trim(),
                Signal: "None",
                Rsi: roundedRsi,
                CandleTime: currentCandle.Timestamp,
                Timeframe: timeframe));
        }

        var backtest = EvaluateBacktest(closes, signalType, options);
        if (!backtest.Passed)
        {
            return Task.FromResult(new StrategySignal(
                StrategyId: "rsi",
                Asset: asset.Trim(),
                Signal: "None",
                Rsi: roundedRsi,
                CandleTime: currentCandle.Timestamp,
                Timeframe: timeframe,
                Backtest: backtest));
        }

        // Instant entry only: if this closed candle is already old, the setup is gone.
        var closedAt = currentCandle.EndTimestamp!.Value;
        var lag = now - closedAt;
        var maxLag = Math.Max(1, options.MaxEntryLagSeconds);
        if (lag > TimeSpan.FromSeconds(maxLag))
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-LAG1",
                "RsiSignalService.GetSignalAsync",
                "signal_stale_engine",
                new
                {
                    asset = asset.Trim(),
                    lagSec = Math.Round(lag.TotalSeconds, 2),
                    maxLagSec = maxLag,
                    candleStart = currentCandle.Timestamp.ToUnixTimeSeconds(),
                    candleEnd = closedAt.ToUnixTimeSeconds(),
                    nowSec = now.ToUnixTimeSeconds(),
                    successRate = backtest.SuccessRate,
                    direction = signalType.ToString()
                });
            // #endregion
            return Task.FromResult(new StrategySignal(
                StrategyId: "rsi",
                Asset: asset.Trim(),
                Signal: "None",
                Rsi: roundedRsi,
                CandleTime: currentCandle.Timestamp,
                Timeframe: timeframe,
                Backtest: backtest,
                AutomationError: "SIGNAL_STALE"));
        }

        var key = EmissionKey(userId, asset, options.TimeframeSeconds);
        if (_lastEmittedSignalCandleTime.TryGetValue(key, out var lastTime) &&
            lastTime == currentCandle.Timestamp)
        {
            // Already traded this closed candle — no re-emit.
            return Task.FromResult(new StrategySignal(
                StrategyId: "rsi",
                Asset: asset.Trim(),
                Signal: "None",
                Rsi: roundedRsi,
                CandleTime: currentCandle.Timestamp,
                Timeframe: timeframe,
                Backtest: backtest));
        }

        // Do NOT record emission here — analyze-then-execute needs a second Call/Put.
        return Task.FromResult(new StrategySignal(
            StrategyId: "rsi",
            Asset: asset.Trim(),
            Signal: signalType == RsiSignalType.Call ? "Call" : "Put",
            Rsi: roundedRsi,
            CandleTime: currentCandle.Timestamp,
            Timeframe: timeframe,
            Backtest: backtest));
    }

    public void MarkSignalEmitted(
        Guid userId,
        string asset,
        int timeframeSeconds,
        DateTimeOffset candleTime)
    {
        if (string.IsNullOrWhiteSpace(asset))
            return;
        _lastEmittedSignalCandleTime[EmissionKey(userId, asset, timeframeSeconds)] = candleTime;
    }

    private static string EmissionKey(Guid userId, string asset, int timeframeSeconds) =>
        $"{userId:N}:{asset.Trim().ToUpperInvariant()}:{timeframeSeconds}";

    private RsiBacktestStats EvaluateBacktest(
        IReadOnlyList<decimal> closes,
        RsiSignalType direction,
        RsiStrategyOptions options)
    {
        var currentIndex = closes.Count - 1;
        // The lookback ends before the current entry and before any historical
        // outcome would overlap the current candle. This prevents lookahead.
        var firstIndex = Math.Max(options.Period, currentIndex - options.BacktestCandleCount);
        var lastIndex = currentIndex - options.ExpiryCandles - 1;
        var total = 0;
        var successful = 0;

        for (var entryIndex = firstIndex; entryIndex <= lastIndex; entryIndex++)
        {
            var historicalRsi = _calculator.CalculateRsi(
                closes.Take(entryIndex + 1).ToList(), options);
            var matchesDirection = direction == RsiSignalType.Call
                ? historicalRsi <= options.Oversold
                : historicalRsi >= options.Overbought;

            if (!matchesDirection)
                continue;

            total++;
            var entryClose = closes[entryIndex];
            var expiryClose = closes[entryIndex + options.ExpiryCandles];
            var won = direction == RsiSignalType.Call
                ? expiryClose > entryClose
                : expiryClose < entryClose;
            if (won)
                successful++;
        }

        var failed = total - successful;
        var successRate = total == 0
            ? 0m
            : Math.Round(successful * 100m / total, 2, MidpointRounding.AwayFromZero);
        return new RsiBacktestStats(
            TotalSignals: total,
            SuccessfulSignals: successful,
            FailedSignals: failed,
            SuccessRate: successRate,
            LookbackCandles: options.BacktestCandleCount,
            ExpiryCandles: options.ExpiryCandles,
            MinimumSuccessRate: options.MinimumSuccessRate,
            Passed: total > 0 && successRate >= options.MinimumSuccessRate);
    }

    private static void ValidateOptions(RsiStrategyOptions options)
    {
        if (options.TimeframeSeconds != 60)
            throw new ApiException(ApiErrorCodes.ValidationError, "RSI Smart Backtest only supports the 1-minute timeframe.");
        if (options.Period is < 2 or > 100)
            throw new ApiException(ApiErrorCodes.ValidationError, "RSI length must be between 2 and 100.");
        if (options.Oversold is <= 0 or >= 100 || options.Overbought is <= 0 or >= 100 || options.Oversold >= options.Overbought)
            throw new ApiException(ApiErrorCodes.ValidationError, "RSI levels must be between 0 and 100, with oversold below overbought.");
        if (options.BacktestCandleCount is < 20 or > 2000)
            throw new ApiException(ApiErrorCodes.ValidationError, "Backtest candles must be between 20 and 2000.");
        if (options.ExpiryCandles is < 3 or > 5)
            throw new ApiException(ApiErrorCodes.ValidationError, "Expiry must be 3, 4, or 5 candles.");
        if (options.MinimumSuccessRate is < 0 or > 100)
            throw new ApiException(ApiErrorCodes.ValidationError, "Minimum success rate must be between 0 and 100.");
        if (options.MaxEntryLagSeconds is < 1 or > 120)
            throw new ApiException(ApiErrorCodes.ValidationError, "Max entry lag must be between 1 and 120 seconds.");
    }
}
