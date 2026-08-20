using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Strategies;

public sealed class RsiSignalService : IRsiSignalService
{
    private readonly IRsiCalculator _calculator;

    // First-seen closed setup per user+asset. Expired or consumed until a new closed extreme.
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

        // Closed 1m bars only: a bar is closed when its minute has elapsed.
        // Binolla wire EndTimestamp is the last tick, not the period close — never use it
        // to treat the forming minute as a completed 1m candle.
        var periodSeconds = options.TimeframeSeconds;
        var barLength = TimeSpan.FromSeconds(periodSeconds);
        var ordered = candles
            .OrderBy(c => c.Timestamp)
            .GroupBy(c => c.Timestamp.ToUnixTimeSeconds() / periodSeconds)
            .Select(g => g.Last())
            .ToList();
        var closed = ordered
            .Where(c => c.Timestamp + barLength <= now)
            .ToList();

        // One current candle, a completed expiry window for at least one
        // historical entry, and enough closes for Wilder RSI.
        if (closed.Count < options.Period + options.ExpiryCandles + 2)
            throw new ApiException(ApiErrorCodes.ValidationError, "Insufficient closed candles for RSI backtest.");

        var currentCandle = closed[^1];
        var closeTime = currentCandle.Timestamp + barLength;
        var closes = closed.Select(c => c.Close).ToList();
        var currentRsi = _calculator.CalculateRsi(closes, options);
        // Backtest is always ready before the closed RSI gate is consulted.
        var (callBacktest, putBacktest) = RsiZoneBacktest.Evaluate(closes, options);

        var roundedRsi = RsiEntryLevels.AlignToBinolla(
            Math.Round(currentRsi, 2, MidpointRounding.AwayFromZero));
        var forming = ordered.LastOrDefault(c => c.Timestamp + barLength > now);
        var liveCloses = forming is null
            ? closes
            : closes.Concat(new[] { forming.Close }).ToList();
        var liveRsi = liveCloses.Count >= options.Period + 1
            ? RsiEntryLevels.AlignToBinolla(
                Math.Round(_calculator.CalculateRsi(liveCloses, options), 2, MidpointRounding.AwayFromZero))
            : roundedRsi;

        // Closed RSI is the primary gate. Live/forming RSI is display-only —
        // a touch of 75/25 that closes back inside the range does not enter.
        RsiSignalType signalType;
        RsiBacktestStats? entryBacktest;
        string? midSkip;
        if (RsiEntryLevels.IsCallRsi(roundedRsi))
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
        else if (RsiEntryLevels.IsPutRsi(roundedRsi))
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
            // Closed mid-range — wick on forming bar has no entry value.
            signalType = RsiSignalType.None;
            entryBacktest = null;
            midSkip = "midRsi";
        }

        var secondsSinceClose = (now - closeTime).TotalSeconds;
        if (signalType != RsiSignalType.None &&
            (secondsSinceClose < 0 || secondsSinceClose > RsiEntryLevels.SetupTtlSeconds))
        {
            // Extreme closed candle is already older than the entry window.
            midSkip = "closeLag";
            signalType = RsiSignalType.None;
            entryBacktest = null;
        }

        var touchedOversold = RsiEntryLevels.IsCallRsi(roundedRsi);
        var touchedOverbought = RsiEntryLevels.IsPutRsi(roundedRsi);

        var timeframe = options.TimeframeSeconds.ToString();
        // Display: when closed at extreme show that side's backtest; otherwise show either for info only.
        var displayBacktest = touchedOversold ? callBacktest
            : touchedOverbought ? putBacktest
            : roundedRsi >= 50m ? putBacktest : callBacktest;
        var key = EmissionKey(userId, asset, options.TimeframeSeconds);

        if (signalType == RsiSignalType.None || entryBacktest is null)
        {
            _setups.TryRemove(key, out _);
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-LIVE",
                "RsiSignalService.GetSignalAsync",
                "closed_entry_eval",
                new
                {
                    asset = asset.Trim(),
                    rsi = roundedRsi,
                    liveRsi,
                    lastClosedClose = currentCandle.Close,
                    lastClosedStart = currentCandle.Timestamp.ToUnixTimeSeconds(),
                    closeTimeUnix = closeTime.ToUnixTimeSeconds(),
                    secondsSinceClose = Math.Round(secondsSinceClose, 1),
                    formingClose = forming?.Close,
                    formingStart = forming?.Timestamp.ToUnixTimeSeconds(),
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
                CandleTime: closeTime,
                Timeframe: timeframe,
                Backtest: displayBacktest,
                LiveRsi: liveRsi,
                AutomationError: midSkip == "closeLag" ? "SETUP_EXPIRED" : null));
        }

        var watch = _setups.AddOrUpdate(
            key,
            _ => new SetupWatch(signalType, currentCandle.Timestamp, closeTime, Consumed: false),
            (_, existing) => existing.Side == signalType && existing.CandleStart == currentCandle.Timestamp
                ? existing
                : new SetupWatch(signalType, currentCandle.Timestamp, closeTime, Consumed: false));
        var ageSeconds = (now - watch.CloseTime).TotalSeconds;
        var fresh = !watch.Consumed && ageSeconds <= RsiEntryLevels.SetupTtlSeconds;

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-LIVE",
            "RsiSignalService.GetSignalAsync",
            "closed_entry_eval",
            new
            {
                asset = asset.Trim(),
                rsi = roundedRsi,
                liveRsi,
                lastClosedClose = currentCandle.Close,
                lastClosedStart = currentCandle.Timestamp.ToUnixTimeSeconds(),
                closeTimeUnix = closeTime.ToUnixTimeSeconds(),
                secondsSinceClose = Math.Round(secondsSinceClose, 1),
                formingClose = forming?.Close,
                formingStart = forming?.Timestamp.ToUnixTimeSeconds(),
                hasForming = forming is not null,
                rsiSide = signalType.ToString(),
                lookback = RsiZoneBacktest.LookbackCandles,
                touchedOversold,
                touchedOverbought,
                ageSeconds = Math.Round(ageSeconds, 2),
                consumed = watch.Consumed,
                wouldEnter = fresh,
                putOk = RsiEntryLevels.IsPutRsi(roundedRsi),
                callOk = RsiEntryLevels.IsCallRsi(roundedRsi),
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
                CandleTime: watch.CloseTime,
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
            CandleTime: watch.CloseTime,
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
            _ => new SetupWatch(RsiSignalType.None, candleTime, candleTime, Consumed: true),
            (_, existing) => existing with { Consumed = true });
    }

    private readonly record struct SetupWatch(
        RsiSignalType Side,
        DateTimeOffset CandleStart,
        DateTimeOffset CloseTime,
        bool Consumed);

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
