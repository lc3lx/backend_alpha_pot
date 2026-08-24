using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;
using Xunit;

namespace ScarAlpha.Binolla.Tests;

public class MinuteBarsTests
{
    [Fact]
    public void Wire_last_tick_end_becomes_period_close()
    {
        var candles = new[]
        {
            new CandlestickData
            {
                Timestamp = 1748947440,
                Open = 0.5295,
                Low = 0.52919,
                High = 0.52969,
                Close = 0.52919,
                EndTimestamp = 1748947499.879
            },
            new CandlestickData
            {
                Timestamp = 1748947500,
                Open = 0.52969,
                Low = 0.52962,
                High = 0.5297,
                Close = 0.52959,
                EndTimestamp = 1748947504.926
            }
        };

        var normalized = MinuteBars.Normalize(candles, 60);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(1748947440, normalized[0].Timestamp);
        Assert.Equal(1748947500, normalized[0].EndTimestamp);
        Assert.Equal(1748947500, normalized[1].Timestamp);
        Assert.Equal(1748947560, normalized[1].EndTimestamp);
    }

    [Fact]
    public void Ticks_fill_minutes_missing_from_official_dump()
    {
        var t0 = 1_740_000_000L;
        var official = new[]
        {
            new CandlestickData { Timestamp = t0, Open = 10, High = 11, Low = 9, Close = 10.5 }
        };
        var ticks = new[]
        {
            new TickData { Timestamp = t0 + 10, Price = 10.6 },
            new TickData { Timestamp = t0 + 60, Price = 10.7 },
            new TickData { Timestamp = t0 + 90, Price = 10.8 },
            new TickData { Timestamp = t0 + 120, Price = 10.4 }
        };

        // "now" sits inside the t0+120 bar, so t0 and t0+60 are settled.
        var now = DateTimeOffset.FromUnixTimeSeconds(t0 + 150);
        var merged = MinuteBars.MergeOfficialAndTicks(official, ticks, 60, now);

        Assert.Equal(3, merged.Count);
        Assert.Equal(t0, merged[0].Timestamp);
        // The t0 period has elapsed, so Binolla's own close stands and the 10.6 tick
        // only widens the range. Overriding it here is what drifted RSI off the chart.
        Assert.Equal(10.5, merged[0].Close);
        Assert.Equal(11, merged[0].High);
        Assert.Equal(t0 + 60, merged[1].Timestamp);
        Assert.Equal(10.8, merged[1].Close);
        Assert.Equal(t0 + 120, merged[2].Timestamp);
        Assert.Equal(10.4, merged[2].Close);
        Assert.Equal(t0 + 180, merged[2].EndTimestamp);
    }

    [Fact]
    public void Quote_rolls_the_next_1m_bar_and_freezes_the_previous_close()
    {
        var t0 = 1_740_000_000L;
        var candles = MinuteBars.Normalize(
            new[]
            {
                new CandlestickData { Timestamp = t0, Open = 10, High = 11, Low = 9, Close = 10.5 }
            },
            60);

        var updated = MinuteBars.ApplyQuote(
            candles, 60, t0 + 60.4, 10.9, DateTimeOffset.FromUnixTimeSeconds(t0 + 61));

        Assert.Equal(2, updated.Count);
        Assert.Equal(10.5, updated[0].Close);
        Assert.Equal(t0 + 60, updated[1].Timestamp);
        Assert.Equal(10.9, updated[1].Close);
        Assert.Equal(t0 + 120, updated[1].EndTimestamp);
    }

    [Fact]
    public void Gap_does_not_invent_missing_1m_closes()
    {
        var t0 = 1_740_000_000L;
        var candles = MinuteBars.Normalize(
            new[]
            {
                new CandlestickData { Timestamp = t0, Open = 10, High = 11, Low = 9, Close = 10.5 }
            },
            60);

        var updated = MinuteBars.ApplyQuote(
            candles, 60, t0 + 180, 11, DateTimeOffset.FromUnixTimeSeconds(t0 + 200));

        Assert.Single(updated);
        Assert.Equal(10.5, updated[0].Close);
    }

    [Fact]
    public void Forming_current_minute_is_not_stale()
    {
        var t0 = 1_740_000_000L;
        var now = DateTimeOffset.FromUnixTimeSeconds(t0 + 30);
        var candles = MinuteBars.Normalize(
            new[]
            {
                new CandlestickData { Timestamp = t0, Open = 10, High = 11, Low = 9, Close = 10.5 }
            },
            60);

        Assert.False(MinuteBars.IsSeriesStale(candles, 60, now));
    }

    [Fact]
    public void Closed_bar_older_than_one_minute_is_stale()
    {
        var t0 = 1_740_000_000L;
        var now = DateTimeOffset.FromUnixTimeSeconds(t0 + 130);
        var candles = MinuteBars.Normalize(
            new[]
            {
                new CandlestickData { Timestamp = t0, Open = 10, High = 11, Low = 9, Close = 10.5 }
            },
            60);

        Assert.True(MinuteBars.IsSeriesStale(candles, 60, now));
    }

    [Fact]
    public void Ticks_never_overwrite_the_close_of_a_period_that_already_ended()
    {
        // The root cause of "bot RSI 76, chart RSI 71": a tick close replacing the
        // broker's settled close on the newest bar, which carries full weight in RSI.
        var t0 = 1_740_000_000L;
        var official = new[]
        {
            new CandlestickData { Timestamp = t0, Open = 10, High = 10.6, Low = 9.9, Close = 10.5 }
        };
        var ticks = new[] { new TickData { Timestamp = t0 + 55, Price = 10.2 } };

        var closed = MinuteBars.MergeOfficialAndTicks(
            official, ticks, 60, DateTimeOffset.FromUnixTimeSeconds(t0 + 90));
        Assert.Equal(10.5, closed[0].Close);

        // Still forming: the tick is newer than the official snapshot, so it wins.
        var forming = MinuteBars.MergeOfficialAndTicks(
            official, ticks, 60, DateTimeOffset.FromUnixTimeSeconds(t0 + 58));
        Assert.Equal(10.2, forming[0].Close);
    }

    [Fact]
    public void A_late_quote_cannot_move_a_settled_close()
    {
        var t0 = 1_740_000_000L;
        var candles = MinuteBars.Normalize(
            new[]
            {
                new CandlestickData { Timestamp = t0, Open = 10, High = 11, Low = 9, Close = 10.5 }
            },
            60);

        // Quote timestamped inside t0's period, but arriving after that period closed.
        var updated = MinuteBars.ApplyQuote(
            candles, 60, t0 + 59, 12.0, DateTimeOffset.FromUnixTimeSeconds(t0 + 75));

        Assert.Single(updated);
        Assert.Equal(10.5, updated[0].Close);
        Assert.Equal(11, updated[0].High);
    }
}
