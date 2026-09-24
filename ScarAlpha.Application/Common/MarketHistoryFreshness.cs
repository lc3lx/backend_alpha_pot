namespace ScarAlpha.Application.Common;

/// <summary>
/// Whether a candle series is still advancing. Fetching history is not enough — a dump
/// of old bars must not look "warm" or the warmup worker will skip re-subscribing and
/// the bot goes blind once the browser is closed.
/// </summary>
public static class MarketHistoryFreshness
{
    public static bool IsFresh(DateTimeOffset lastBarEnd, DateTimeOffset now, int periodSeconds)
    {
        if (periodSeconds <= 0) return false;
        var slack = TimeSpan.FromSeconds(periodSeconds + 15);
        return now - lastBarEnd < slack && now >= lastBarEnd.AddSeconds(-periodSeconds);
    }
}
