using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The Pine corner table, ported: entry close vs close after tradeDurationBars,
/// plus the checkpointBars probe.
/// </summary>
public sealed class EmaRsiTrackerTests
{
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string Asset = "EURUSD_otc";
    private static readonly DateTimeOffset EntryBarClose = new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Long_that_closes_higher_after_the_duration_is_a_win()
    {
        var tracker = new EmaRsiTradeTracker();
        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose, checkpointBars: 2, durationBars: 3);

        // Bars closing at 10:01 .. 10:03; the 3rd closes above the entry close.
        tracker.Resolve(UserId, Asset, Bars(101m, 102m, 103m));

        var stats = tracker.GetStats(UserId);
        stats.Wins.Should().Be(1);
        stats.Losses.Should().Be(0);
        stats.WinRate.Should().Be(100m);
    }

    [Fact]
    public void Long_that_closes_lower_is_a_loss()
    {
        var tracker = new EmaRsiTradeTracker();
        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose, checkpointBars: 2, durationBars: 3);

        tracker.Resolve(UserId, Asset, Bars(101m, 102m, 99m));

        tracker.GetStats(UserId).Losses.Should().Be(1);
        tracker.GetStats(UserId).Wins.Should().Be(0);
    }

    [Fact]
    public void Short_wins_only_when_price_closes_below_entry()
    {
        var tracker = new EmaRsiTradeTracker();
        tracker.RecordEntry(UserId, Asset, "Put", 100m, EntryBarClose, checkpointBars: 2, durationBars: 3);

        tracker.Resolve(UserId, Asset, Bars(99m, 98m, 97m));

        tracker.GetStats(UserId).Wins.Should().Be(1);
    }

    [Fact]
    public void An_unchanged_close_counts_as_a_loss_like_pine()
    {
        var tracker = new EmaRsiTradeTracker();
        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose, checkpointBars: 2, durationBars: 3);

        // Pine: `close > entryPrice` — exactly equal is not a win.
        tracker.Resolve(UserId, Asset, Bars(100m, 100m, 100m));

        tracker.GetStats(UserId).Losses.Should().Be(1);
    }

    [Fact]
    public void Checkpoint_is_scored_separately_at_its_own_bar()
    {
        var tracker = new EmaRsiTradeTracker();
        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose, checkpointBars: 2, durationBars: 3);

        // Winning at the 2-bar checkpoint, losing by settlement.
        tracker.Resolve(UserId, Asset, Bars(101m, 105m, 98m));

        var stats = tracker.GetStats(UserId);
        stats.CheckpointWins.Should().Be(1);
        stats.CheckpointLosses.Should().Be(0);
        stats.Losses.Should().Be(1);
        stats.Wins.Should().Be(0);
    }

    [Fact]
    public void Nothing_is_scored_before_the_bars_exist()
    {
        var tracker = new EmaRsiTradeTracker();
        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose, checkpointBars: 2, durationBars: 3);

        // Only the first bar after entry has closed.
        tracker.Resolve(UserId, Asset, Bars(101m));

        var stats = tracker.GetStats(UserId);
        stats.Wins.Should().Be(0);
        stats.Losses.Should().Be(0);
        stats.CheckpointWins.Should().Be(0);
    }

    [Fact]
    public void Repeated_polling_scores_each_trade_exactly_once()
    {
        var tracker = new EmaRsiTradeTracker();
        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose, checkpointBars: 2, durationBars: 3);
        var bars = Bars(101m, 102m, 103m);

        // The worker polls every second — the same closed series is seen many times.
        for (var i = 0; i < 10; i++)
            tracker.Resolve(UserId, Asset, bars);

        var stats = tracker.GetStats(UserId);
        stats.Wins.Should().Be(1);
        stats.CheckpointWins.Should().Be(1);
    }

    [Fact]
    public void Counters_accumulate_across_trades_and_reset_on_request()
    {
        var tracker = new EmaRsiTradeTracker();

        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose, 2, 3);
        tracker.Resolve(UserId, Asset, Bars(101m, 102m, 103m));

        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose.AddMinutes(10), 2, 3);
        tracker.Resolve(UserId, Asset, Bars(EntryBarClose.AddMinutes(10), 99m, 98m, 97m));

        var stats = tracker.GetStats(UserId);
        stats.Wins.Should().Be(1);
        stats.Losses.Should().Be(1);
        stats.WinRate.Should().Be(50m);

        tracker.Reset(UserId);
        tracker.GetStats(UserId).Should().Be(new EmaRsiStats(0, 0, 0, 0, 0m));
    }

    [Fact]
    public void Users_do_not_share_counters()
    {
        var tracker = new EmaRsiTradeTracker();
        var other = Guid.Parse("44444444-4444-4444-4444-444444444444");

        tracker.RecordEntry(UserId, Asset, "Call", 100m, EntryBarClose, 2, 3);
        tracker.Resolve(UserId, Asset, Bars(101m, 102m, 103m));

        tracker.GetStats(UserId).Wins.Should().Be(1);
        tracker.GetStats(other).Wins.Should().Be(0);
    }

    /// <summary>Bars closing at EntryBarClose + 1min, +2min, … one per supplied close.</summary>
    private static List<RsiCandle> Bars(params decimal[] closes) => Bars(EntryBarClose, closes);

    private static List<RsiCandle> Bars(DateTimeOffset entryBarClose, params decimal[] closes) =>
        closes
            .Select((c, i) => new RsiCandle(entryBarClose.AddMinutes(i), c, entryBarClose.AddMinutes(i + 1)))
            .ToList();
}
