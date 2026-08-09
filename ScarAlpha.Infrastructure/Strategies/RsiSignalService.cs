using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Strategies;

public sealed class RsiSignalService : IRsiSignalService
{
    private readonly IRsiCalculator _calculator;

    // Tracks last non-NONE signal candleTime per user+asset+timeframe, to avoid repeating signals for the same candle.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastNonNoneSignalCandleTime = new();

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

        var closed = candles
            .Where(c => c.EndTimestamp is null || c.EndTimestamp <= now)
            .OrderBy(c => c.Timestamp)
            .ToList();

        // Need at least (period + 2) closes: current RSI uses `period + 1`,
        // previous RSI uses `period + 1` too, shifted by one candle.
        if (closed.Count < options.Period + 2)
            throw new ApiException(ApiErrorCodes.ValidationError, "Insufficient candles for RSI signal.");

        var currentCandle = closed[^1];
        var previousCandle = closed[^2];

        var closes = closed.Select(c => c.Close).ToList();
        var prevRsi = _calculator.CalculateRsi(closes.Take(closed.Count - 1).ToList(), options);
        var currentRsi = _calculator.CalculateRsi(closes, options);

        // Crossing logic (no new signals unless crossing threshold boundaries).
        var call = previousCandle is not null && prevRsi <= options.Oversold && currentRsi > options.Oversold;
        var put = previousCandle is not null && prevRsi >= options.Overbought && currentRsi < options.Overbought;

        var signalType = call ? RsiSignalType.Call :
            put ? RsiSignalType.Put :
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

        var key = $"{userId:N}:{asset.Trim().ToUpperInvariant()}:{options.TimeframeSeconds}";
        if (_lastNonNoneSignalCandleTime.TryGetValue(key, out var lastTime) &&
            lastTime == currentCandle.Timestamp)
        {
            // No repeat for the same candle.
            return Task.FromResult(new StrategySignal(
                StrategyId: "rsi",
                Asset: asset.Trim(),
                Signal: "None",
                Rsi: roundedRsi,
                CandleTime: currentCandle.Timestamp,
                Timeframe: timeframe));
        }

        _lastNonNoneSignalCandleTime[key] = currentCandle.Timestamp;

        return Task.FromResult(new StrategySignal(
            StrategyId: "rsi",
            Asset: asset.Trim(),
            Signal: signalType == RsiSignalType.Call ? "Call" : "Put",
            Rsi: roundedRsi,
            CandleTime: currentCandle.Timestamp,
            Timeframe: timeframe));
    }
}

