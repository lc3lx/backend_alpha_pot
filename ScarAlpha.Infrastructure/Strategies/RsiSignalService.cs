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
        var (callBacktest, putBacktest) = EvaluateBothBacktests(closes, options);

        // The candle must CLOSE inside the relevant RSI extreme. Touching the
        // level intra-candle is never visible here because open candles were
        // removed above.
        var signalType = currentRsi <= options.Oversold ? RsiSignalType.Call :
            currentRsi >= options.Overbought ? RsiSignalType.Put :
            RsiSignalType.None;

        var roundedRsi = Math.Round(currentRsi, 2, MidpointRounding.AwayFromZero);
        var liveCloses = candles.OrderBy(c => c.Timestamp).Select(c => c.Close).ToList();
        var liveRsi = liveCloses.Count >= options.Period + 1
            ? Math.Round(_calculator.CalculateRsi(liveCloses, options), 2, MidpointRounding.AwayFromZero)
            : roundedRsi;
        var timeframe = options.TimeframeSeconds.ToString();
        // Always expose a live backtest: the side RSI is leaning toward.
        var displayBacktest = signalType == RsiSignalType.Call ? callBacktest
            : signalType == RsiSignalType.Put ? putBacktest
            : currentRsi >= 50m ? putBacktest : callBacktest;
        var entryBacktest = signalType == RsiSignalType.Call ? callBacktest
            : signalType == RsiSignalType.Put ? putBacktest
            : displayBacktest;

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-BT1",
            "RsiSignalService.GetSignalAsync",
            "backtest_always",
            new
            {
                asset = asset.Trim(),
                rsi = roundedRsi,
                liveRsi,
                rsiEqual = liveRsi == roundedRsi,
                rsiSide = signalType.ToString(),
                lookback = options.BacktestCandleCount,
                callRate = callBacktest.SuccessRate,
                callN = callBacktest.TotalSignals,
                callPass = callBacktest.Passed,
                putRate = putBacktest.SuccessRate,
                putN = putBacktest.TotalSignals,
                putPass = putBacktest.Passed
            },
            runId: "backtest-always");
        // #endregion

        if (signalType == RsiSignalType.None || !entryBacktest.Passed)
        {
            return Task.FromResult(new StrategySignal(
                StrategyId: "rsi",
                Asset: asset.Trim(),
                Signal: "None",
                Rsi: roundedRsi,
                CandleTime: currentCandle.Timestamp,
                Timeframe: timeframe,
                Backtest: displayBacktest,
                LiveRsi: liveRsi));
        }

        var backtest = entryBacktest;

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
                AutomationError: "SIGNAL_STALE",
                LiveRsi: liveRsi));
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
                Backtest: backtest,
                LiveRsi: liveRsi));
        }

        // Do NOT record emission here — analyze-then-execute needs a second Call/Put.
        return Task.FromResult(new StrategySignal(
            StrategyId: "rsi",
            Asset: asset.Trim(),
            Signal: signalType == RsiSignalType.Call ? "Call" : "Put",
            Rsi: roundedRsi,
            CandleTime: currentCandle.Timestamp,
            Timeframe: timeframe,
            Backtest: backtest,
            LiveRsi: liveRsi));
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

    private (RsiBacktestStats Call, RsiBacktestStats Put) EvaluateBothBacktests(
        IReadOnlyList<decimal> closes,
        RsiStrategyOptions options)
    {
        var currentIndex = closes.Count - 1;
        var firstIndex = Math.Max(options.Period, currentIndex - options.BacktestCandleCount);
        var lastIndex = currentIndex - 1;
        var rsiSeries = BuildRsiSeries(closes, options);
        var callTotal = 0;
        var callWins = 0;
        var putTotal = 0;
        var putWins = 0;
        var inCallZone = false;
        var inPutZone = false;
        var callTouchIndex = -1;
        var putTouchIndex = -1;

        for (var i = firstIndex; i <= lastIndex; i++)
        {
            var historicalRsi = rsiSeries[i];

            if (historicalRsi <= options.Oversold)
            {
                if (!inCallZone)
                {
                    inCallZone = true;
                    callTouchIndex = i;
                }
            }
            else if (inCallZone)
            {
                callTotal++;
                if (closes[i] > closes[callTouchIndex])
                    callWins++;
                inCallZone = false;
                callTouchIndex = -1;
            }

            if (historicalRsi >= options.Overbought)
            {
                if (!inPutZone)
                {
                    inPutZone = true;
                    putTouchIndex = i;
                }
            }
            else if (inPutZone)
            {
                putTotal++;
                if (closes[i] < closes[putTouchIndex])
                    putWins++;
                inPutZone = false;
                putTouchIndex = -1;
            }
        }

        return (ToStats(callTotal, callWins, options), ToStats(putTotal, putWins, options));
    }

    private static decimal[] BuildRsiSeries(IReadOnlyList<decimal> closes, RsiStrategyOptions options)
    {
        var series = new decimal[closes.Count];
        if (closes.Count < options.Period + 1)
            return series;

        decimal gainSum = 0m;
        decimal lossSum = 0m;
        for (var i = 1; i <= options.Period; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta > 0) gainSum += delta;
            else lossSum += -delta;
        }

        var avgGain = gainSum / options.Period;
        var avgLoss = lossSum / options.Period;
        series[options.Period] = ToRsi(avgGain, avgLoss);

        for (var i = options.Period + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var gain = delta > 0 ? delta : 0m;
            var loss = delta < 0 ? -delta : 0m;
            avgGain = (avgGain * (options.Period - 1) + gain) / options.Period;
            avgLoss = (avgLoss * (options.Period - 1) + loss) / options.Period;
            series[i] = ToRsi(avgGain, avgLoss);
        }

        return series;
    }

    private static decimal ToRsi(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0m)
            return avgGain == 0m ? 50m : 100m;
        var rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    private static RsiBacktestStats ToStats(int total, int wins, RsiStrategyOptions options)
    {
        var failed = total - wins;
        var successRate = total == 0
            ? 0m
            : Math.Round(wins * 100m / total, 2, MidpointRounding.AwayFromZero);
        return new RsiBacktestStats(
            TotalSignals: total,
            SuccessfulSignals: wins,
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
