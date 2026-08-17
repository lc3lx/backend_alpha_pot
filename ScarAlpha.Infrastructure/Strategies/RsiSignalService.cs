using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Strategies;

public sealed class RsiSignalService : IRsiSignalService
{
    private readonly IRsiCalculator _calculator;

    // First-seen live setup per user+asset. Expired or consumed until RSI leaves the zone.
    private readonly ConcurrentDictionary<string, SetupWatch> _setups = new();

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

        options = options with
        {
            Oversold = RsiEntryLevels.CallMax,
            Overbought = RsiEntryLevels.PutMin,
            BacktestCandleCount = RsiZoneBacktest.LookbackCandles,
            MinimumSuccessRate = RsiEntryLevels.MinSuccessRate,
            MaxEntryLagSeconds = RsiEntryLevels.SetupTtlSeconds
        };
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
        // Backtest is always ready before the live RSI gate is consulted.
        var (callBacktest, putBacktest) = RsiZoneBacktest.Evaluate(closes, options);

        var roundedRsi = Math.Round(currentRsi, 2, MidpointRounding.AwayFromZero);
        var liveCloses = candles.OrderBy(c => c.Timestamp).Select(c => c.Close).ToList();
        var liveRsi = liveCloses.Count >= options.Period + 1
            ? Math.Round(_calculator.CalculateRsi(liveCloses, options), 2, MidpointRounding.AwayFromZero)
            : roundedRsi;
        var forming = candles
            .Where(c => c.EndTimestamp is null || c.EndTimestamp > now)
            .OrderBy(c => c.Timestamp)
            .LastOrDefault();

        // Live RSI is the primary gate. Backtest is computed in parallel but ignored for entry
        // until RSI is ≤25 (Call) or ≥75 (Put). Only then does matching backtest matter.
        // Without a forming bar (fresh quote), closed-only RSI must not trigger entries.
        RsiSignalType signalType;
        RsiBacktestStats? entryBacktest;
        string? midSkip;
        if (forming is null)
        {
            signalType = RsiSignalType.None;
            entryBacktest = null;
            midSkip = "noLiveQuote";
        }
        else if (RsiEntryLevels.IsCallRsi(liveRsi))
        {
            if (RsiEntryLevels.BacktestOk(callBacktest))
            {
                signalType = RsiSignalType.Call;
                entryBacktest = callBacktest;
                midSkip = null;
            }
            else
            {
                signalType = RsiSignalType.None;
                entryBacktest = null;
                midSkip = "backtest";
            }
        }
        else if (RsiEntryLevels.IsPutRsi(liveRsi))
        {
            if (RsiEntryLevels.BacktestOk(putBacktest))
            {
                signalType = RsiSignalType.Put;
                entryBacktest = putBacktest;
                midSkip = null;
            }
            else
            {
                signalType = RsiSignalType.None;
                entryBacktest = null;
                midSkip = "backtest";
            }
        }
        else
        {
            // Mid-range live RSI — backtest has no entry value.
            signalType = RsiSignalType.None;
            entryBacktest = null;
            midSkip = "midRsi";
        }

        var touchedOversold = RsiEntryLevels.IsCallRsi(liveRsi);
        var touchedOverbought = RsiEntryLevels.IsPutRsi(liveRsi);

        var timeframe = options.TimeframeSeconds.ToString();
        // Display: when at extreme show that side's backtest; otherwise show either for info only.
        var displayBacktest = touchedOversold ? callBacktest
            : touchedOverbought ? putBacktest
            : liveRsi >= 50m ? putBacktest : callBacktest;
        var key = EmissionKey(userId, asset, options.TimeframeSeconds);

        if (signalType == RsiSignalType.None || entryBacktest is null)
        {
            _setups.TryRemove(key, out _);
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-LIVE",
                "RsiSignalService.GetSignalAsync",
                "live_entry_eval",
                new
                {
                    asset = asset.Trim(),
                    rsi = roundedRsi,
                    liveRsi,
                    lastClosedClose = currentCandle.Close,
                    formingClose = forming?.Close,
                    hasForming = forming is not null,
                    rsiSide = "None",
                    lookback = RsiZoneBacktest.LookbackCandles,
                    touchedOversold,
                    touchedOverbought,
                    wouldEnter = false,
                    skip = midSkip,
                    callRate = callBacktest.SuccessRate,
                    callN = callBacktest.TotalSignals,
                    callPass = callBacktest.Passed,
                    putRate = putBacktest.SuccessRate,
                    putN = putBacktest.TotalSignals,
                    putPass = putBacktest.Passed
                },
                runId: "missed-entry");
            // #endregion
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

        var watch = _setups.AddOrUpdate(
            key,
            _ => new SetupWatch(signalType, now, Consumed: false),
            (_, existing) => existing.Side == signalType
                ? existing
                : new SetupWatch(signalType, now, Consumed: false));
        var ageSeconds = (now - watch.FirstSeenAt).TotalSeconds;
        var fresh = !watch.Consumed && ageSeconds <= RsiEntryLevels.SetupTtlSeconds;

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-LIVE",
            "RsiSignalService.GetSignalAsync",
            "live_entry_eval",
            new
            {
                asset = asset.Trim(),
                    rsi = roundedRsi,
                    liveRsi,
                    lastClosedClose = currentCandle.Close,
                    formingClose = forming?.Close,
                    hasForming = forming is not null,
                    rsiSide = signalType.ToString(),
                lookback = RsiZoneBacktest.LookbackCandles,
                touchedOversold,
                touchedOverbought,
                ageSeconds = Math.Round(ageSeconds, 2),
                consumed = watch.Consumed,
                wouldEnter = fresh,
                putOk = RsiEntryLevels.IsPutRsi(liveRsi),
                callOk = RsiEntryLevels.IsCallRsi(liveRsi),
                skip = fresh
                    ? "ok"
                    : watch.Consumed ? "consumed" : "expired",
                callRate = callBacktest.SuccessRate,
                callN = callBacktest.TotalSignals,
                callPass = callBacktest.Passed,
                putRate = putBacktest.SuccessRate,
                putN = putBacktest.TotalSignals,
                putPass = putBacktest.Passed
            },
            runId: "missed-entry");
        // #endregion

        if (!fresh)
        {
            return Task.FromResult(new StrategySignal(
                StrategyId: "rsi",
                Asset: asset.Trim(),
                Signal: "None",
                Rsi: roundedRsi,
                CandleTime: watch.FirstSeenAt,
                Timeframe: timeframe,
                Backtest: entryBacktest,
                LiveRsi: liveRsi,
                AutomationError: watch.Consumed ? "SETUP_CONSUMED" : "SETUP_EXPIRED"));
        }

        return Task.FromResult(new StrategySignal(
            StrategyId: "rsi",
            Asset: asset.Trim(),
            Signal: signalType == RsiSignalType.Call ? "Call" : "Put",
            Rsi: roundedRsi,
            CandleTime: watch.FirstSeenAt,
            Timeframe: timeframe,
            Backtest: entryBacktest,
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
        _setups.AddOrUpdate(
            EmissionKey(userId, asset, timeframeSeconds),
            _ => new SetupWatch(RsiSignalType.None, candleTime, Consumed: true),
            (_, existing) => existing with { Consumed = true });
    }

    private readonly record struct SetupWatch(RsiSignalType Side, DateTimeOffset FirstSeenAt, bool Consumed);

    private static string EmissionKey(Guid userId, string asset, int timeframeSeconds) =>
        $"{userId:N}:{asset.Trim().ToUpperInvariant()}:{timeframeSeconds}";

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
