using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Strategies;

/// <summary>
/// Runs the alternating-candles rule against live closed 5-minute bars.
///
/// Same entry discipline as the other engines: the setup is born when the candle that
/// completed the pattern CLOSES, is valid only for a few seconds after that, and is
/// consumed once a trade is placed — so one pattern can never open two trades.
/// </summary>
public sealed class AlternatingSignalService : IAlternatingSignalService
{
    private readonly ConcurrentDictionary<string, SetupWatch> _setups = new();

    public Task<StrategySignal> GetSignalAsync(
        Guid userId,
        string asset,
        IReadOnlyList<RsiCandle> candles,
        AlternatingOptions options,
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

        // This rule reads raw candle bodies, so it needs no indicator warmup — just
        // the pattern itself plus one bar of margin.
        if (closed.Count < options.PatternLength + 1)
            throw new ApiException(ApiErrorCodes.ValidationError, "Insufficient closed candles for the alternating pattern.");

        var currentCandle = closed[^1];
        var closeTime = currentCandle.Timestamp + barLength;
        var eval = AlternatingCandlesEngine.Evaluate(closed, options);

        var timeframe = periodSeconds.ToString();
        var key = EmissionKey(userId, asset, periodSeconds);
        var secondsSinceClose = (now - closeTime).TotalSeconds;

        if (eval.Signal == "None")
        {
            _setups.TryRemove(key, out _);
            return Task.FromResult(Build(asset, "None", eval, closeTime, timeframe, options, eval.SkipReason));
        }

        if (secondsSinceClose < 0 || secondsSinceClose > options.EffectiveEntryLagSeconds)
            return Task.FromResult(Build(asset, "None", eval, closeTime, timeframe, options, "SETUP_EXPIRED"));

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
    /// This rule has no backtest; the stats slot carries the expiry so the executor
    /// derives the same contract duration it does for the other engines.
    /// </summary>
    private static StrategySignal Build(
        string asset,
        string signal,
        AlternatingEvaluation eval,
        DateTimeOffset candleTime,
        string timeframe,
        AlternatingOptions options,
        string? automationError) =>
        new(
            StrategyId: AlternatingStrategy.Id,
            Asset: asset.Trim(),
            Signal: signal,
            Rsi: 0m,
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
            AutomationError: automationError);

    private readonly record struct SetupWatch(
        string Side,
        DateTimeOffset CandleStart,
        DateTimeOffset CloseTime,
        bool Consumed);

    private static string EmissionKey(Guid userId, string asset, int timeframeSeconds) =>
        $"{userId:N}:{asset.Trim().ToUpperInvariant()}:{timeframeSeconds}";

    private static void ValidateOptions(AlternatingOptions options)
    {
        if (options.TimeframeSeconds != 300)
            throw new ApiException(ApiErrorCodes.ValidationError, "The alternating strategy runs on the 5-minute timeframe.");
        if (options.PatternLength is < 2 or > 10)
            throw new ApiException(ApiErrorCodes.ValidationError, "Pattern length must be between 2 and 10 candles.");
        if (options.ExpiryCandles is < 1 or > 3)
            throw new ApiException(ApiErrorCodes.ValidationError, "Expiry must be between 1 and 3 candles.");
        if (options.MaxEntryLagSeconds is < 0 or > 120)
            throw new ApiException(ApiErrorCodes.ValidationError, "Max entry lag must be between 1 and 120 seconds.");
    }
}
