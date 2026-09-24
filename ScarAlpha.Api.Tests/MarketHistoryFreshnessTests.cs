using FluentAssertions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class MarketHistoryFreshnessTests
{
    [Fact]
    public void A_bar_that_just_closed_is_fresh()
    {
        var end = new DateTimeOffset(2026, 9, 21, 15, 19, 0, TimeSpan.Zero);
        MarketHistoryFreshness.IsFresh(end, end.AddSeconds(10), 60).Should().BeTrue();
    }

    [Fact]
    public void A_series_idle_for_three_minutes_is_stale()
    {
        var end = new DateTimeOffset(2026, 9, 21, 15, 1, 0, TimeSpan.Zero);
        MarketHistoryFreshness.IsFresh(end, end.AddSeconds(1046), 60).Should().BeFalse();
    }
}
