using FluentAssertions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class LiveBoardOverlayTests
{
    [Fact]
    public void A_live_tick_moves_rsi_off_the_closed_bar()
    {
        var closes = Enumerable.Range(0, 80)
            .Select(i => 1.1440m + (i % 7 - 3) * 0.00012m)
            .ToList();
        var last = closes[^1];
        var closed = LiveBoardOverlay.Rsi(closes, livePrice: null);
        var up = LiveBoardOverlay.Rsi(closes, last + 0.0008m);
        var down = LiveBoardOverlay.Rsi(closes, last - 0.0008m);

        closed.Should().BeGreaterThan(0m);
        up.Should().NotBe(closed);
        down.Should().NotBe(closed);
        up.Should().BeGreaterThan(down);
    }
}
