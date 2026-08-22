namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Range- and volume-based indicators. These need whole bars, unlike
/// <see cref="Indicators"/>, which works on closes alone.
///
/// <para>All smoothing is Wilder's, matching <see cref="Indicators.RsiSeries"/>, so a
/// value only settles after roughly <c>2 x length</c> bars — see
/// <see cref="IndicatorWarmup"/>. Feeding a short series produces numbers no chart
/// agrees with.</para>
///
/// <para>Bars without a real high/low fall back to the close (<see cref="RsiCandle.HighOrClose"/>).
/// That collapses true range toward zero, which is why callers should check
/// <see cref="HasUsableRange"/> before trusting a volatility reading.</para>
/// </summary>
public static class Volatility
{
    /// <summary>ADX with its two directional components, for one bar.</summary>
    public readonly record struct AdxPoint(decimal Adx, decimal PlusDi, decimal MinusDi)
    {
        /// <summary>Positive when buyers dominate, negative when sellers do.</summary>
        public decimal DirectionalBias => PlusDi - MinusDi;
    }

    /// <summary>True when enough bars carry a genuine high/low to trust range math.</summary>
    public static bool HasUsableRange(IReadOnlyList<RsiCandle> bars, int minimumRatio = 80)
    {
        if (bars is null || bars.Count == 0) return false;
        var withRange = bars.Count(b => b.HasRange);
        return withRange * 100 >= bars.Count * minimumRatio;
    }

    /// <summary>
    /// Wilder true range: the largest of the bar's own range and the two gaps to the
    /// previous close. Index 0 is 0 (no previous bar).
    /// </summary>
    public static decimal[] TrueRangeSeries(IReadOnlyList<RsiCandle> bars)
    {
        if (bars is null) throw new ArgumentNullException(nameof(bars));

        var tr = new decimal[bars.Count];
        for (var i = 1; i < bars.Count; i++)
        {
            var high = bars[i].HighOrClose;
            var low = bars[i].LowOrClose;
            var prevClose = bars[i - 1].Close;

            var range = high - low;
            var upGap = Math.Abs(high - prevClose);
            var downGap = Math.Abs(low - prevClose);
            tr[i] = Math.Max(range, Math.Max(upGap, downGap));
        }

        return tr;
    }

    /// <summary>
    /// Wilder ATR. Entries before <paramref name="length"/> are 0 (undefined).
    /// </summary>
    public static decimal[] AtrSeries(IReadOnlyList<RsiCandle> bars, int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        var atr = new decimal[bars?.Count ?? 0];
        if (bars is null || bars.Count < length + 1) return atr;

        var tr = TrueRangeSeries(bars);

        decimal seed = 0m;
        for (var i = 1; i <= length; i++)
            seed += tr[i];
        var current = seed / length;
        atr[length] = current;

        for (var i = length + 1; i < bars.Count; i++)
        {
            current = (current * (length - 1) + tr[i]) / length;
            atr[i] = current;
        }

        return atr;
    }

    /// <summary>Last ATR value, or null when there is not enough history.</summary>
    public static decimal? Atr(IReadOnlyList<RsiCandle> bars, int length)
    {
        if (bars is null || bars.Count < length + 1) return null;
        return AtrSeries(bars, length)[^1];
    }

    /// <summary>
    /// Wilder ADX with +DI / -DI. ADX needs two smoothing passes, so the first real
    /// value lands at index <c>2 * length - 1</c>; everything before that is default.
    /// </summary>
    public static AdxPoint[] AdxSeries(IReadOnlyList<RsiCandle> bars, int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        var result = new AdxPoint[bars?.Count ?? 0];
        if (bars is null || bars.Count < 2 * length) return result;

        var tr = TrueRangeSeries(bars);
        var plusDm = new decimal[bars.Count];
        var minusDm = new decimal[bars.Count];

        for (var i = 1; i < bars.Count; i++)
        {
            var upMove = bars[i].HighOrClose - bars[i - 1].HighOrClose;
            var downMove = bars[i - 1].LowOrClose - bars[i].LowOrClose;

            // Only the larger move counts, and only when it is positive.
            plusDm[i] = upMove > downMove && upMove > 0m ? upMove : 0m;
            minusDm[i] = downMove > upMove && downMove > 0m ? downMove : 0m;
        }

        // Wilder's running sums, seeded with the first `length` values.
        decimal trSum = 0m, plusSum = 0m, minusSum = 0m;
        for (var i = 1; i <= length; i++)
        {
            trSum += tr[i];
            plusSum += plusDm[i];
            minusSum += minusDm[i];
        }

        var dx = new decimal[bars.Count];
        var firstDxIndex = length;

        void WriteDi(int index)
        {
            if (trSum == 0m)
            {
                dx[index] = 0m;
                result[index] = new AdxPoint(0m, 0m, 0m);
                return;
            }

            var plusDi = 100m * plusSum / trSum;
            var minusDi = 100m * minusSum / trSum;
            var diSum = plusDi + minusDi;
            dx[index] = diSum == 0m ? 0m : 100m * Math.Abs(plusDi - minusDi) / diSum;
            result[index] = new AdxPoint(0m, plusDi, minusDi);
        }

        WriteDi(firstDxIndex);

        for (var i = length + 1; i < bars.Count; i++)
        {
            trSum = trSum - trSum / length + tr[i];
            plusSum = plusSum - plusSum / length + plusDm[i];
            minusSum = minusSum - minusSum / length + minusDm[i];
            WriteDi(i);
        }

        // ADX is a Wilder average of DX, seeded over the first `length` DX values.
        var firstAdxIndex = 2 * length - 1;
        if (firstAdxIndex >= bars.Count) return result;

        decimal dxSeed = 0m;
        for (var i = firstDxIndex; i <= firstAdxIndex; i++)
            dxSeed += dx[i];
        var adx = dxSeed / length;
        result[firstAdxIndex] = result[firstAdxIndex] with { Adx = adx };

        for (var i = firstAdxIndex + 1; i < bars.Count; i++)
        {
            adx = (adx * (length - 1) + dx[i]) / length;
            result[i] = result[i] with { Adx = adx };
        }

        return result;
    }

    /// <summary>Last ADX point, or null when there is not enough history.</summary>
    public static AdxPoint? Adx(IReadOnlyList<RsiCandle> bars, int length)
    {
        if (bars is null || bars.Count < 2 * length) return null;
        return AdxSeries(bars, length)[^1];
    }

    /// <summary>
    /// Latest bar's volume against the average of the <paramref name="lookback"/> bars
    /// before it: 1.0 is a normal bar, 0.5 is half the usual activity.
    ///
    /// <para>Returns null when ANY bar in the window has no volume. Binolla leaves volume
    /// empty on forming bars and on narrow history rows, and a missing value is not a
    /// quiet market — callers must treat null as "cannot tell" and skip the check.</para>
    /// </summary>
    public static decimal? RelativeVolume(IReadOnlyList<RsiCandle> bars, int lookback)
    {
        if (lookback <= 0) throw new ArgumentOutOfRangeException(nameof(lookback));
        if (bars is null || bars.Count < lookback + 1) return null;

        var latest = bars[^1].Volume;
        if (latest is null) return null;

        decimal sum = 0m;
        for (var i = bars.Count - 1 - lookback; i < bars.Count - 1; i++)
        {
            if (bars[i].Volume is not decimal v) return null;
            sum += v;
        }

        var average = sum / lookback;
        if (average <= 0m) return null;

        return Math.Round(latest.Value / average, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// ATR as a percentage of price — comparable across pairs, unlike raw ATR.
    /// </summary>
    public static decimal? AtrPercent(IReadOnlyList<RsiCandle> bars, int length)
    {
        var atr = Atr(bars, length);
        if (atr is null) return null;

        var price = bars[^1].Close;
        if (price <= 0m) return null;

        return Math.Round(atr.Value * 100m / price, 6, MidpointRounding.AwayFromZero);
    }
}
