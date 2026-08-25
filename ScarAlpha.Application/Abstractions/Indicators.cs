namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// TradingView/Pine-compatible indicator math, shared by every strategy.
///
/// Parity rules that make these values match a broker chart (Binolla, TradingView, …):
///  * <c>ta.rsi</c> is Wilder smoothing (RMA) seeded with the simple average of the
///    first <c>length</c> deltas — <see cref="RsiSeries"/>.
///  * <c>ta.ema</c> seeds with the SMA of the first <c>length</c> values, then applies
///    alpha = 2/(length+1) — <see cref="EmaSeries"/>.
///  * Both are recursive: the value depends on how much history you feed them.
///    Feeding 20 bars and feeding 300 bars produce DIFFERENT numbers for the same
///    last bar. See <see cref="IndicatorWarmup"/>.
/// </summary>
public static class Indicators
{
    private static decimal _rsiCalibrationOffset;
    private static decimal? _rsiCalibrationOffsetLow;

    /// <summary>Calibration anchors: the offsets are measured near the entry levels.</summary>
    private const decimal LowAnchor = 25m;
    private const decimal HighAnchor = 75m;

    /// <summary>
    /// Offset subtracted from RSI near the OVERBOUGHT end so the bot reads what the
    /// broker chart reads (<c>Strategy:RsiCalibrationOffset</c>, default 0 = none).
    /// </summary>
    public static decimal RsiCalibrationOffset
    {
        get => _rsiCalibrationOffset;
        set => _rsiCalibrationOffset = Validated(value);
    }

    /// <summary>
    /// Offset near the OVERSOLD end (<c>Strategy:RsiCalibrationOffsetLow</c>).
    /// Defaults to the overbought offset when unset.
    ///
    /// <para>Two numbers because the gap is NOT constant. Measured against this broker:
    /// ~6 points near RSI 66, but only ~2 points near RSI 30. A single offset that fixes
    /// the top over-corrects the bottom by 4 — which made CALL fire when the chart still
    /// read 29 instead of 25.</para>
    /// </summary>
    public static decimal RsiCalibrationOffsetLow
    {
        get => _rsiCalibrationOffsetLow ?? _rsiCalibrationOffset;
        set => _rsiCalibrationOffsetLow = Validated(value);
    }

    private static decimal Validated(decimal value) =>
        value is >= -25m and <= 25m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Calibration must be between -25 and 25.");

    /// <summary>
    /// Applies the calibration and keeps the result inside the RSI range.
    ///
    /// <para>The offset is interpolated between the two anchors rather than switched at
    /// a threshold: a step would make the reading jump as RSI crossed the midpoint, and
    /// a jump in the number the entry gate reads is far worse than a small
    /// interpolation error.</para>
    /// </summary>
    private static decimal Calibrate(decimal rsi)
    {
        var low = RsiCalibrationOffsetLow;
        var high = _rsiCalibrationOffset;
        if (low == 0m && high == 0m) return rsi;

        decimal offset;
        if (low == high) offset = high;
        else if (rsi <= LowAnchor) offset = low;
        else if (rsi >= HighAnchor) offset = high;
        else offset = low + (high - low) * (rsi - LowAnchor) / (HighAnchor - LowAnchor);

        var shifted = rsi - offset;
        return shifted < 0m ? 0m : shifted > 100m ? 100m : shifted;
    }

    /// <summary>
    /// Wilder RSI for every index, with any configured calibration applied. Entries
    /// before <paramref name="length"/> are 0 (undefined — never read them).
    /// </summary>
    public static decimal[] RsiSeries(IReadOnlyList<decimal> closes, int length)
    {
        var series = RawRsiSeries(closes, length);
        // Both sides checked: one offset alone being zero must not skip the other.
        if (_rsiCalibrationOffset == 0m && RsiCalibrationOffsetLow == 0m) return series;

        for (var i = length; i < series.Length; i++)
            series[i] = Calibrate(series[i]);
        return series;
    }

    /// <summary>
    /// Wilder RSI with NO calibration — the pure indicator, used to verify the maths
    /// against a reference.
    /// </summary>
    public static decimal[] RawRsiSeries(IReadOnlyList<decimal> closes, int length)
    {
        if (closes is null) throw new ArgumentNullException(nameof(closes));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        var series = new decimal[closes.Count];
        if (closes.Count < length + 1)
            return series;

        decimal gainSum = 0m;
        decimal lossSum = 0m;
        for (var i = 1; i <= length; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta > 0) gainSum += delta;
            else lossSum += -delta;
        }

        var avgGain = gainSum / length;
        var avgLoss = lossSum / length;
        series[length] = ToRsi(avgGain, avgLoss);

        for (var i = length + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var gain = delta > 0 ? delta : 0m;
            var loss = delta < 0 ? -delta : 0m;
            avgGain = (avgGain * (length - 1) + gain) / length;
            avgLoss = (avgLoss * (length - 1) + loss) / length;
            series[i] = ToRsi(avgGain, avgLoss);
        }

        return series;
    }

    /// <summary>Last Wilder RSI value, or null when there is not enough history.</summary>
    public static decimal? Rsi(IReadOnlyList<decimal> closes, int length)
    {
        if (closes.Count < length + 1) return null;
        return RsiSeries(closes, length)[^1];
    }

    private static decimal ToRsi(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0m)
            return avgGain == 0m ? 50m : 100m;
        var rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    /// <summary>
    /// Pine <c>ta.ema</c>: SMA-seeded exponential moving average.
    /// Entries before <paramref name="length"/> - 1 are 0 (undefined).
    /// </summary>
    public static decimal[] EmaSeries(IReadOnlyList<decimal> values, int length)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        var series = new decimal[values.Count];
        if (values.Count < length)
            return series;

        decimal seed = 0m;
        for (var i = 0; i < length; i++)
            seed += values[i];
        var ema = seed / length;
        series[length - 1] = ema;

        var alpha = 2m / (length + 1m);
        for (var i = length; i < values.Count; i++)
        {
            ema = alpha * values[i] + (1m - alpha) * ema;
            series[i] = ema;
        }

        return series;
    }

    /// <summary>Last EMA value, or null when there is not enough history.</summary>
    public static decimal? Ema(IReadOnlyList<decimal> values, int length)
    {
        if (values.Count < length) return null;
        return EmaSeries(values, length)[^1];
    }
}

/// <summary>
/// How much history a recursive indicator needs before its value stops depending
/// on where the series happened to start.
///
/// Wilder RSI carries its seed forward with weight (1 - 1/length) per bar. With
/// length 14 the seed still contributes ~22% after 20 bars and ~0.06% only after
/// ~100 bars. Computing RSI(14) on 20 candles and comparing it to a broker chart
/// (which runs over its full loaded history) can differ by 10+ RSI points — that
/// is exactly the "bot says 78, platform says 71" class of bug, and no constant
/// offset can correct it because the error depends on the seed window.
/// </summary>
public static class IndicatorWarmup
{
    /// <summary>
    /// Bars of warmup required before an RSI value is considered chart-accurate.
    /// (1 - 1/14)^150 ≈ 1.4e-5 — the seed is numerically irrelevant by then.
    /// </summary>
    public const int DefaultMinRsiCandles = 150;

    private static int _minRsiCandles = DefaultMinRsiCandles;

    /// <summary>
    /// Tuning knob for brokers that push shallow history. Lowering it trades chart
    /// parity for earlier signals: at 60 bars the Wilder seed still contributes ~2%,
    /// which is roughly a 1–3 point RSI difference from the platform. Set once at
    /// startup (see appsettings <c>Strategy:MinRsiWarmupCandles</c>); read-only after.
    /// </summary>
    public static int MinRsiCandles
    {
        get => _minRsiCandles;
        set => _minRsiCandles = value is >= 20 and <= 500
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Warmup must be between 20 and 500 candles.");
    }

    /// <summary>Warmup multiple applied to any EMA length (EMA200 ⇒ 200 bars minimum).</summary>
    public const int EmaWarmupMultiplier = 1;

    /// <summary>Minimum closed candles needed to trust <c>RSI(length)</c>.</summary>
    public static int ForRsi(int length) => Math.Max(MinRsiCandles, length + 1);

    /// <summary>Minimum closed candles needed to trust <c>EMA(length)</c>.</summary>
    public static int ForEma(int length) => length * EmaWarmupMultiplier + 1;

    /// <summary>Minimum closed candles needed to trust <c>ATR(length)</c>.</summary>
    public static int ForAtr(int length) => Math.Max(MinRsiCandles, length + 1);

    /// <summary>
    /// Minimum closed candles needed to trust <c>ADX(length)</c>. ADX smooths twice, so
    /// it needs about double — still under the RSI floor, which means regime detection
    /// costs no extra history at all.
    /// </summary>
    public static int ForAdx(int length) => Math.Max(MinRsiCandles, 2 * length + 1);
}
