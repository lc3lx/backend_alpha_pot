namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Turns a raw candle dump into the exact series a broker chart draws:
/// closed bars only, one bar per period, and no time gaps.
///
/// Gaps matter. RSI/EMA are recursive over consecutive deltas, so a series that
/// silently skips minutes (missed WS pushes, session breaks, weekend on non-OTC
/// pairs) folds one large jump into the average instead of several small ones and
/// drifts away from the chart value. We therefore keep only the longest CONTIGUOUS
/// run that ends at the most recent closed bar.
/// </summary>
public static class CandleSeries
{
    public readonly record struct Prepared(
        IReadOnlyList<RsiCandle> Closed,
        RsiCandle? Forming,
        int RawCount,
        int GapsDropped)
    {
        public int Count => Closed.Count;
        public RsiCandle Last => Closed[^1];
        public IReadOnlyList<decimal> Closes => Closed.Select(c => c.Close).ToList();
    }

    /// <summary>
    /// Deduplicates by period bucket, splits closed vs forming, and trims to the
    /// contiguous tail.
    /// </summary>
    public static Prepared Prepare(
        IReadOnlyList<RsiCandle> candles,
        int periodSeconds,
        DateTimeOffset now)
    {
        if (candles is null) throw new ArgumentNullException(nameof(candles));
        if (periodSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(periodSeconds));

        var barLength = TimeSpan.FromSeconds(periodSeconds);

        // One bar per bucket, ascending. Later entries win (they are the fresher tick).
        // Each bucket's timestamp is normalized to exact bucket start to prevent fractional second drift.
        var ordered = candles
            .OrderBy(c => c.Timestamp)
            .GroupBy(c => c.Timestamp.ToUnixTimeSeconds() / periodSeconds)
            .Select(g =>
            {
                var last = g.Last();
                var bucketStartUnix = (last.Timestamp.ToUnixTimeSeconds() / periodSeconds) * periodSeconds;
                var bucketStart = DateTimeOffset.FromUnixTimeSeconds(bucketStartUnix);
                return last with { Timestamp = bucketStart, EndTimestamp = bucketStart + barLength };
            })
            .OrderBy(c => c.Timestamp)
            .ToList();

        // A bar is closed only once its whole period has elapsed. Binolla's wire
        // EndTimestamp is the last tick inside the bar, never the period close.
        var closed = ordered.Where(c => c.Timestamp + barLength <= now).ToList();
        var forming = ordered.LastOrDefault(c => c.Timestamp + barLength > now);

        var filled = FillSmallGaps(closed, periodSeconds);
        var contiguous = ContiguousTail(filled, periodSeconds);
        return new Prepared(
            Closed: contiguous,
            Forming: forming,
            RawCount: closed.Count,
            GapsDropped: filled.Count - contiguous.Count);
    }

    /// <summary>
    /// Maximum gap size (in seconds) to forward-fill. A dropped Quotex session can
    /// leave a multi-hour hole with live ticks on top; filling up to 6 hours keeps
    /// RSI computable until history/load backfills real bars. Larger gaps (weekend
    /// on non-OTC) stay untouched so ContiguousTail stops at them.
    /// </summary>
    public const int MaxFillGapSeconds = 21600;

    /// <summary>
    /// Forward-fills small gaps between closed bars with the previous close.
    /// Larger gaps (e.g. market closure) are left untouched so ContiguousTail
    /// can stop at them cleanly.
    /// </summary>
    public static List<RsiCandle> FillSmallGaps(IReadOnlyList<RsiCandle> closed, int periodSeconds)
    {
        if (closed.Count <= 1) return closed.ToList();

        var barLength = TimeSpan.FromSeconds(periodSeconds);
        var result = new List<RsiCandle>(closed.Count + 16);
        result.Add(closed[0]);

        for (var i = 1; i < closed.Count; i++)
        {
            var prev = result[^1];
            var curr = closed[i];
            var gapSeconds = (curr.Timestamp - prev.Timestamp).TotalSeconds;

            if (gapSeconds > periodSeconds + 0.5d && gapSeconds <= MaxFillGapSeconds)
            {
                var nextTime = prev.Timestamp + barLength;
                while (nextTime + TimeSpan.FromSeconds(0.5d) < curr.Timestamp)
                {
                    result.Add(new RsiCandle(
                        Timestamp: nextTime,
                        Close: prev.Close,
                        EndTimestamp: nextTime + barLength,
                        High: prev.Close,
                        Low: prev.Close,
                        Open: prev.Close,
                        Volume: 0m));
                    nextTime += barLength;
                }
            }

            result.Add(curr);
        }

        return result;
    }

    /// <summary>
    /// Longest run of back-to-back periods ending at the newest closed bar.
    /// </summary>
    public static List<RsiCandle> ContiguousTail(IReadOnlyList<RsiCandle> closed, int periodSeconds)
    {
        if (closed.Count == 0) return new List<RsiCandle>();

        var start = closed.Count - 1;
        for (var i = closed.Count - 1; i > 0; i--)
        {
            var deltaSeconds = (closed[i].Timestamp - closed[i - 1].Timestamp).TotalSeconds;
            if (Math.Abs(deltaSeconds - periodSeconds) > 0.5d)
                break;
            start = i - 1;
        }

        var tail = new List<RsiCandle>(closed.Count - start);
        for (var i = start; i < closed.Count; i++)
            tail.Add(closed[i]);
        return tail;
    }
}
