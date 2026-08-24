using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Strategies;

/// <summary>
/// Runs the ported Pine strategy against live closed bars.
///
/// Mirrors <see cref="RsiSignalService"/>'s entry discipline: a setup is born when the
/// bar that produced the cross CLOSES, is valid only for
/// <see cref="EmaRsiOptions.MaxEntryLagSeconds"/> after that, and is consumed once a
/// trade is actually placed — so one cross can never open two trades.
/// </summary>
public sealed class EmaRsiSignalService : IEmaRsiSignalService
{
    private readonly ConcurrentDictionary<string, SetupWatch> _setups = new();

    public Task<StrategySignal> GetSignalAsync(
        Guid userId,
        string asset,
        IReadOnlyList<RsiCandle> candles,
        IReadOnlyList<decimal> trendCloses,
        EmaRsiOptions options,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (candles is null)
            throw new ArgumentNullException(nameof(candles));
        ValidateOptions(options);

        var periodSeconds = options.TimeframeSeconds;
        var barLength = TimeSpan.FromSeconds(periodSeconds);
        var prepared = CandleSeries.Prepare(candles, periodSeconds, now);
        var closed = prepared.Closed;

        // EMA and RSI are both recursive — short history means values the chart
        // does not agree with, and a cross that never really happened.
        if (closed.Count < options.RequiredCandles)
            throw new ApiException(ApiErrorCodes.ValidationError, "Insufficient closed candles for EMA/RSI strategy.");

        var currentCandle = closed[^1];
        var closeTime = currentCandle.Timestamp + barLength;
        var closes = prepared.Closes;

        var trend = EmaRsiEngine.ResolveTrend(trendCloses, options);
        var eval = EmaRsiEngine.Evaluate(closes, options, trend);

        var timeframe = periodSeconds.ToString();
        var key = EmissionKey(userId, asset, periodSeconds);
        var secondsSinceClose = (now - closeTime).TotalSeconds;

        if (eval.Signal == "None")
        {
            _setups.TryRemove(key, out _);
            return Task.FromResult(Build(asset, "None", eval, closeTime, timeframe, options, eval.SkipReason));
        }

        // The cross bar has closed but the entry window already passed.
        if (secondsSinceClose < 0 || secondsSinceClose > options.EffectiveEntryLagSeconds)
        {
            return Task.FromResult(Build(asset, "None", eval, closeTime, timeframe, options, "SETUP_EXPIRED"));
        }

        var watch = _setups.AddOrUpdate(
            key,
            _ => new SetupWatch(eval.Signal, currentCandle.Timestamp, closeTime, Consumed: false),
            (_, existing) => existing.Side == eval.Signal && existing.CandleStart == currentCandle.Timestamp
                ? existing
                : new SetupWatch(eval.Signal, currentCandle.Timestamp, closeTime, Consumed: false));

        var ageSeconds = (now - watch.CloseTime).TotalSeconds;
        var fresh = !watch.Consumed && ageSeconds <= options.EffectiveEntryLagSeconds;
        if (!fresh)
        {
            return Task.FromResult(Build(
                asset, "None", eval, watch.CloseTime, timeframe, options,
                watch.Consumed ? "SETUP_CONSUMED" : "SETUP_EXPIRED"));
        }

        return Task.FromResult(Build(asset, eval.Signal, eval, watch.CloseTime, timeframe, options, null));
    }

    public void MarkSignalEmitted(Guid userId, string asset, int timeframeSeconds, DateTimeOffset candleTime)
    {
        if (string.IsNullOrWhiteSpace(asset)) return;
        _setups.AddOrUpdate(
            EmissionKey(userId, asset, timeframeSeconds),
            _ => new SetupWatch("None", candleTime, candleTime, Consumed: true),
            (_, existing) => existing with { Consumed = true });
    }

    /// <summary>
    /// The EMA strategy has no zone backtest; the stats slot carries the expiry so the
    /// executor derives the same contract duration it does for RSI.
    /// </summary>
    private static StrategySignal Build(
        string asset,
        string signal,
        EmaRsiEvaluation eval,
        DateTimeOffset candleTime,
        string timeframe,
        EmaRsiOptions options,
        string? automationError) =>
        new(
            StrategyId: "ema",
            Asset: asset.Trim(),
            Signal: signal,
            Rsi: eval.Rsi,
            CandleTime: candleTime,
            Timeframe: timeframe,
            Backtest: new RsiBacktestStats(
                TotalSignals: 0,
                SuccessfulSignals: 0,
                FailedSignals: 0,
                SuccessRate: 0m,
                LookbackCandles: 0,
                ExpiryCandles: options.ExpiryCandles,
                MinimumSuccessRate: 0m,
                Passed: true),
            AutomationError: automationError,
            LiveRsi: eval.Rsi);

    private readonly record struct SetupWatch(
        string Side,
        DateTimeOffset CandleStart,
        DateTimeOffset CloseTime,
        bool Consumed);

    private static string EmissionKey(Guid userId, string asset, int timeframeSeconds) =>
        $"{userId:N}:{asset.Trim().ToUpperInvariant()}:{timeframeSeconds}";

    private static void ValidateOptions(EmaRsiOptions options)
    {
        if (options.TimeframeSeconds != 60)
            throw new ApiException(ApiErrorCodes.ValidationError, "EMA 9/21 strategy only supports the 1-minute timeframe.");
        if (options.FastLength < 2 || options.SlowLength < 2 || options.FastLength >= options.SlowLength)
            throw new ApiException(ApiErrorCodes.ValidationError, "EMA fast length must be below slow length.");
        if (options.RsiLength is < 2 or > 100)
            throw new ApiException(ApiErrorCodes.ValidationError, "RSI length must be between 2 and 100.");
        if (options.RsiBuyMax is <= 0 or >= 100 || options.RsiSellMin is <= 0 or >= 100)
            throw new ApiException(ApiErrorCodes.ValidationError, "RSI entry bounds must be between 0 and 100.");
        if (options.ExpiryCandles is < 3 or > 5)
            throw new ApiException(ApiErrorCodes.ValidationError, "Expiry must be 3, 4, or 5 candles.");
        if (options.MaxEntryLagSeconds is < 0 or > 120)
            throw new ApiException(ApiErrorCodes.ValidationError, "Max entry lag must be between 1 and 120 seconds.");
    }
}
