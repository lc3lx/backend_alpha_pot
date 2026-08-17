using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Binolla.Protocol;

/// <summary>
/// Binolla 1m RSI must use exchange 1-minute closes.
/// Wire <c>EndTimestamp</c> is the last tick in the bar, not the period close, and
/// <c>s_history/last</c> candles can lag a few minutes — ticks + live quotes fill that gap.
/// </summary>
public static class MinuteBars
{
    public const int DefaultPeriodSeconds = 60;
    public const int MaxStoredCandles = 300;

    public static int EffectivePeriod(int period) =>
        period <= 0 ? DefaultPeriodSeconds : period;

    public static long ToUnixSeconds(double timestamp)
    {
        if (timestamp >= 1_000_000_000_000d)
            return (long)(timestamp / 1000d);
        return (long)Math.Floor(timestamp);
    }

    public static long BucketStartUnix(double timestamp, int periodSeconds)
    {
        var period = EffectivePeriod(periodSeconds);
        var sec = ToUnixSeconds(timestamp);
        return sec / period * period;
    }

    public static bool IsSeriesStale(
        IReadOnlyList<CandlestickData> candles,
        int periodSeconds,
        DateTimeOffset now)
    {
        if (candles is null || candles.Count == 0)
            return true;

        var period = EffectivePeriod(periodSeconds);
        var last = candles[0];
        foreach (var candle in candles)
        {
            if (candle.Timestamp >= last.Timestamp)
                last = candle;
        }

        var lastStart = DateTimeOffset.FromUnixTimeSeconds(BucketStartUnix(last.Timestamp, period));
        var lastEnd = lastStart.AddSeconds(period);
        if (lastEnd > now)
            return (now - lastStart).TotalSeconds > period + 15;

        // Last stored bar already closed — one missed minute is the max before refetch.
        return (now - lastEnd).TotalSeconds > period;
    }

    public static List<CandlestickData> Normalize(
        IEnumerable<CandlestickData> candles,
        int periodSeconds)
    {
        var period = EffectivePeriod(periodSeconds);
        var byStart = new SortedDictionary<long, CandlestickData>();
        foreach (var candle in candles)
        {
            var start = BucketStartUnix(candle.Timestamp, period);
            byStart[start] = new CandlestickData
            {
                Timestamp = start,
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close,
                Volume = candle.Volume,
                EndTimestamp = start + period
            };
        }

        var list = byStart.Values.ToList();
        if (list.Count > MaxStoredCandles)
            list = list.Skip(list.Count - MaxStoredCandles).ToList();
        return list;
    }

    public static List<CandlestickData> SynthesizeFromTicks(
        IEnumerable<TickData> ticks,
        int periodSeconds)
    {
        var period = EffectivePeriod(periodSeconds);
        long? curBucket = null;
        double open = 0, high = 0, low = 0, close = 0;
        var bars = new List<CandlestickData>();
        foreach (var tick in ticks.OrderBy(t => t.Timestamp))
        {
            var bucket = BucketStartUnix(tick.Timestamp, period);
            if (curBucket is null || curBucket != bucket)
            {
                if (curBucket is not null)
                    bars.Add(Bar(curBucket.Value, open, high, low, close, period));
                open = high = low = close = tick.Price;
                curBucket = bucket;
            }
            else
            {
                high = Math.Max(high, tick.Price);
                low = Math.Min(low, tick.Price);
                close = tick.Price;
            }
        }

        if (curBucket is not null)
            bars.Add(Bar(curBucket.Value, open, high, low, close, period));

        return Normalize(bars, period);
    }

    /// <summary>
    /// Official 1m candles plus tick-built bars after (and including) the last official minute.
    /// Fills the lag between <c>s_history/last</c> and the live chart.
    /// </summary>
    public static List<CandlestickData> MergeOfficialAndTicks(
        IReadOnlyList<CandlestickData> official,
        IEnumerable<TickData> ticks,
        int periodSeconds)
    {
        var period = EffectivePeriod(periodSeconds);
        var series = Normalize(official, period);
        var fromTicks = SynthesizeFromTicks(ticks, period);
        if (fromTicks.Count == 0)
            return series;
        if (series.Count == 0)
            return fromTicks;

        var lastStart = (long)series[^1].Timestamp;
        foreach (var bar in fromTicks)
        {
            var start = (long)bar.Timestamp;
            if (start < lastStart)
                continue;
            if (start == lastStart)
                series[^1] = CombineSameMinute(series[^1], bar, period);
            else
            {
                series.Add(bar);
                lastStart = start;
            }
        }

        if (series.Count > MaxStoredCandles)
            series = series.Skip(series.Count - MaxStoredCandles).ToList();
        return series;
    }

    public static List<CandlestickData> ApplyQuote(
        IReadOnlyList<CandlestickData> candles,
        int periodSeconds,
        double quoteTimestamp,
        double price)
    {
        var period = EffectivePeriod(periodSeconds);
        var bucket = BucketStartUnix(quoteTimestamp, period);
        var list = candles.Count == 0
            ? new List<CandlestickData>()
            : Normalize(candles, period);

        if (list.Count == 0)
        {
            list.Add(Bar(bucket, price, price, price, price, period));
            return list;
        }

        var lastStart = (long)list[^1].Timestamp;
        if (bucket < lastStart)
            return list;

        if (bucket == lastStart)
        {
            var last = list[^1];
            list[^1] = new CandlestickData
            {
                Timestamp = last.Timestamp,
                Open = last.Open,
                High = Math.Max(last.High, price),
                Low = Math.Min(last.Low, price),
                Close = price,
                Volume = last.Volume,
                EndTimestamp = lastStart + period
            };
            return list;
        }

        if (bucket == lastStart + period)
        {
            list.Add(Bar(bucket, price, price, price, price, period));
            if (list.Count > MaxStoredCandles)
                list.RemoveRange(0, list.Count - MaxStoredCandles);
            return list;
        }

        // Missing minutes — do not invent 1m closes. Caller should refetch history.
        return list;
    }

    private static CandlestickData CombineSameMinute(
        CandlestickData official,
        CandlestickData fromTicks,
        int period)
    {
        var start = (long)official.Timestamp;
        return new CandlestickData
        {
            Timestamp = start,
            Open = official.Open,
            High = Math.Max(official.High, fromTicks.High),
            Low = Math.Min(official.Low, fromTicks.Low),
            Close = fromTicks.Close,
            Volume = official.Volume ?? fromTicks.Volume,
            EndTimestamp = start + period
        };
    }

    private static CandlestickData Bar(
        long start,
        double open,
        double high,
        double low,
        double close,
        int period) =>
        new()
        {
            Timestamp = start,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            EndTimestamp = start + period
        };
}
