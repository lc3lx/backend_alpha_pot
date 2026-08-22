namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Port of the "EMA 9/21 + RSI Scalping" Pine v5 strategy.
///
/// Pine source of truth:
///   longCond  = ta.crossover(emaFast, emaSlow)  and rsi &lt;= rsiBuyMax  and trendUp
///   shortCond = ta.crossunder(emaFast, emaSlow) and rsi &gt;= rsiSellMin and trendDown
///   exit      = fixed, tradeDurationBars after entry
///
/// Binary-options mapping: Pine's fixed-bar exit IS the option expiry, so
/// <c>tradeDurationBars</c> becomes the contract duration (3–5 minutes on 1m).
/// There is no stop/target to port — the trade always runs to expiry.
///
/// The cross must be detected on CLOSED bars only. A forming bar can cross and
/// un-cross before it closes; entering on that is the classic repaint bug, so the
/// forming bar is never allowed to trigger.
/// </summary>
public sealed record EmaRsiOptions(
    int FastLength = 9,
    int SlowLength = 21,
    int RsiLength = 14,
    /// <summary>Pine: rsiBuyMax — highest RSI still allowed to buy.</summary>
    decimal RsiBuyMax = 60m,
    /// <summary>Pine: rsiSellMin — lowest RSI still allowed to sell.</summary>
    decimal RsiSellMin = 40m,
    int TimeframeSeconds = 60,
    /// <summary>Pine: tradeDurationBars — bars held, i.e. the option expiry in minutes.</summary>
    int ExpiryCandles = 5,
    /// <summary>Pine: useTrend — require the higher-timeframe EMA200 to agree.</summary>
    bool UseTrendFilter = true,
    /// <summary>Pine: htfTF — trend timeframe, default 15m.</summary>
    int TrendTimeframeSeconds = 900,
    int TrendEmaLength = 200,
    /// <summary>Seconds after the bar closes in which the entry is still valid.</summary>
    int MaxEntryLagSeconds = 5)
{
    public static EmaRsiOptions Default => new();

    /// <summary>Maps bot trade duration (180/240/300) to Pine's tradeDurationBars.</summary>
    public static EmaRsiOptions FromBotDurationSeconds(int durationSeconds)
    {
        var candles = durationSeconds / 60;
        if (candles is < 3 or > 5)
            candles = 5;
        return Default with { ExpiryCandles = candles };
    }

    /// <summary>Closed 1m bars needed before any value is chart-accurate.</summary>
    public int RequiredCandles => Math.Max(
        IndicatorWarmup.ForRsi(RsiLength),
        IndicatorWarmup.ForEma(SlowLength));
}

public enum EmaRsiTrend
{
    /// <summary>Higher-timeframe data was unavailable — the filter could not be applied.</summary>
    Unknown,
    Up,
    Down
}

/// <summary>Everything the EMA/RSI rule looked at, so a skip can be explained.</summary>
public sealed record EmaRsiEvaluation(
    string Signal,
    decimal EmaFast,
    decimal EmaSlow,
    decimal PrevEmaFast,
    decimal PrevEmaSlow,
    decimal Rsi,
    bool CrossedUp,
    bool CrossedDown,
    EmaRsiTrend Trend,
    string? SkipReason);

public static class EmaRsiEngine
{
    /// <summary>
    /// Evaluates the rule on the CLOSED series. <paramref name="closes"/> must be
    /// gap-free and end at the last closed bar.
    /// </summary>
    public static EmaRsiEvaluation Evaluate(
        IReadOnlyList<decimal> closes,
        EmaRsiOptions options,
        EmaRsiTrend trend)
    {
        if (closes is null) throw new ArgumentNullException(nameof(closes));

        var fast = Indicators.EmaSeries(closes, options.FastLength);
        var slow = Indicators.EmaSeries(closes, options.SlowLength);
        var rsiSeries = Indicators.RsiSeries(closes, options.RsiLength);

        var i = closes.Count - 1;
        var emaFast = fast[i];
        var emaSlow = slow[i];
        var prevFast = fast[i - 1];
        var prevSlow = slow[i - 1];
        var rsi = Math.Round(rsiSeries[i], 2, MidpointRounding.AwayFromZero);

        // ta.crossover / ta.crossunder — strictly on the two most recent CLOSED bars.
        var crossedUp = prevFast <= prevSlow && emaFast > emaSlow;
        var crossedDown = prevFast >= prevSlow && emaFast < emaSlow;

        string signal;
        string? skip;
        if (crossedUp)
        {
            if (rsi > options.RsiBuyMax)
            {
                signal = "None";
                skip = "RSI_ABOVE_BUY_MAX";
            }
            else if (options.UseTrendFilter && trend != EmaRsiTrend.Up)
            {
                signal = "None";
                skip = trend == EmaRsiTrend.Unknown ? "TREND_UNKNOWN" : "TREND_DOWN";
            }
            else
            {
                signal = "Call";
                skip = null;
            }
        }
        else if (crossedDown)
        {
            if (rsi < options.RsiSellMin)
            {
                signal = "None";
                skip = "RSI_BELOW_SELL_MIN";
            }
            else if (options.UseTrendFilter && trend != EmaRsiTrend.Down)
            {
                signal = "None";
                skip = trend == EmaRsiTrend.Unknown ? "TREND_UNKNOWN" : "TREND_UP";
            }
            else
            {
                signal = "Put";
                skip = null;
            }
        }
        else
        {
            signal = "None";
            skip = "NO_CROSS";
        }

        return new EmaRsiEvaluation(
            Signal: signal,
            EmaFast: emaFast,
            EmaSlow: emaSlow,
            PrevEmaFast: prevFast,
            PrevEmaSlow: prevSlow,
            Rsi: rsi,
            CrossedUp: crossedUp,
            CrossedDown: crossedDown,
            Trend: trend,
            SkipReason: skip);
    }

    /// <summary>
    /// Pine: <c>htfClose &gt; ta.ema(close, 200)</c> on the trend timeframe.
    /// Returns <see cref="EmaRsiTrend.Unknown"/> when there is not enough history —
    /// callers must treat that as "cannot confirm", never as agreement.
    /// </summary>
    public static EmaRsiTrend ResolveTrend(IReadOnlyList<decimal> trendCloses, EmaRsiOptions options)
    {
        if (trendCloses is null || trendCloses.Count < IndicatorWarmup.ForEma(options.TrendEmaLength))
            return EmaRsiTrend.Unknown;

        var ema = Indicators.Ema(trendCloses, options.TrendEmaLength);
        if (ema is null) return EmaRsiTrend.Unknown;

        var close = trendCloses[^1];
        if (close > ema.Value) return EmaRsiTrend.Up;
        if (close < ema.Value) return EmaRsiTrend.Down;
        return EmaRsiTrend.Unknown;
    }

    /// <summary>
    /// Aggregates a 1m closed series into higher-timeframe closes (last 1m close in
    /// each HTF bucket), so the trend filter needs no second history subscription.
    /// Only buckets that are fully covered by the 1m data are emitted.
    /// </summary>
    public static List<decimal> AggregateCloses(
        IReadOnlyList<RsiCandle> oneMinuteClosed,
        int targetTimeframeSeconds)
    {
        var result = new List<decimal>();
        if (oneMinuteClosed is null || oneMinuteClosed.Count == 0) return result;

        long? bucket = null;
        decimal last = 0m;
        var barsInBucket = 0;
        var required = targetTimeframeSeconds / 60;

        foreach (var candle in oneMinuteClosed)
        {
            var b = candle.Timestamp.ToUnixTimeSeconds() / targetTimeframeSeconds;
            if (bucket is null || b != bucket)
            {
                if (bucket is not null && barsInBucket == required)
                    result.Add(last);
                bucket = b;
                barsInBucket = 0;
            }

            last = candle.Close;
            barsInBucket++;
        }

        if (bucket is not null && barsInBucket == required)
            result.Add(last);

        return result;
    }
}

public interface IEmaRsiSignalService
{
    /// <summary>
    /// EMA 9/21 cross + RSI filter + higher-timeframe trend, evaluated on CLOSED 1m bars.
    /// <paramref name="trendCloses"/> is the trend-timeframe close series; pass an empty
    /// list when it is unavailable and the trend resolves to Unknown.
    /// </summary>
    Task<StrategySignal> GetSignalAsync(
        Guid userId,
        string asset,
        IReadOnlyList<RsiCandle> candles,
        IReadOnlyList<decimal> trendCloses,
        EmaRsiOptions options,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Records that the cross on this closed bar was consumed (trade placed), so the
    /// same cross cannot open a second trade.
    /// </summary>
    void MarkSignalEmitted(Guid userId, string asset, int timeframeSeconds, DateTimeOffset candleTime);
}
