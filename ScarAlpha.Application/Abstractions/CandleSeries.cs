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
        var ordered = candles
            .OrderBy(c => c.Timestamp)
            .GroupBy(c => c.Timestamp.ToUnixTimeSeconds() / periodSeconds)
            .Select(g => g.Last())
            .OrderBy(c => c.Timestamp)
            .ToList();

        // A bar is closed only once its whole period has elapsed. Binolla's wire
        // EndTimestamp is the last tick inside the bar, never the period close.
        var closed = ordered.Where(c => c.Timestamp + barLength <= now).ToList();
        var forming = ordered.LastOrDefault(c => c.Timestamp + barLength > now);

        var contiguous = ContiguousTail(closed, periodSeconds);
        return new Prepared(
            Closed: contiguous,
            Forming: forming,
            RawCount: closed.Count,
            GapsDropped: closed.Count - contiguous.Count);
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
