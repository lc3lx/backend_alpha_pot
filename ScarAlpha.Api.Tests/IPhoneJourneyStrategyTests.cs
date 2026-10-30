using ScarAlpha.Application.Abstractions;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// iPhone Journey's rule and its limits. The rule must agree with the Python tooling it
/// was measured with (tools/backtest/live_parallel.py, rule "rsi-any"), and the limits
/// must hold whatever the client sends — they are the part of this strategy that
/// actually protects the user, since the rule itself has no measured edge.
/// </summary>
public sealed class IPhoneJourneyStrategyTests
{
    private static IReadOnlyList<decimal> Falling(int n, decimal start = 100m, decimal step = 0.5m) =>
        Enumerable.Range(0, n).Select(i => start - i * step).ToList();

    private static IReadOnlyList<decimal> Rising(int n, decimal start = 100m, decimal step = 0.5m) =>
        Enumerable.Range(0, n).Select(i => start + i * step).ToList();

    private static IReadOnlyList<decimal> Choppy(int n) =>
        Enumerable.Range(0, n).Select(i => 100m + (i % 2 == 0 ? 0.05m : -0.05m)).ToList();

    [Fact]
    public void Oversold_is_a_call_and_overbought_a_put()
    {
        Assert.Equal("Call", IPhoneJourneyStrategy.Evaluate(Falling(40)).Signal);
        Assert.Equal("Put", IPhoneJourneyStrategy.Evaluate(Rising(40)).Signal);
    }

    [Fact]
    public void A_market_with_no_extreme_is_not_traded()
    {
        var (signal, _, strength) = IPhoneJourneyStrategy.Evaluate(Choppy(40));
        Assert.Equal("None", signal);
        Assert.Equal(0m, strength);
    }

    [Fact]
    public void Too_little_history_is_not_traded()
    {
        Assert.Equal("None", IPhoneJourneyStrategy.Evaluate(Falling(IPhoneJourneyStrategy.RequiredCandles - 1)).Signal);
    }

    [Fact]
    public void A_signal_older_than_the_entry_window_is_rejected()
    {
        var barStart = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var close = barStart.AddSeconds(60);

        var fresh = IPhoneJourneyStrategy.BuildSignal("EURUSD_otc", Falling(40), barStart, 60, close.AddSeconds(5));
        Assert.Equal("Call", fresh.Signal);
        Assert.Equal(IPhoneJourneyStrategy.ExpiryCandles, fresh.Backtest!.ExpiryCandles);

        var stale = IPhoneJourneyStrategy.BuildSignal(
            "EURUSD_otc", Falling(40), barStart, 60,
            close.AddSeconds(IPhoneJourneyStrategy.EntryWindowSeconds + 1));
        Assert.Equal("None", stale.Signal);
        Assert.Equal("SETUP_EXPIRED", stale.AutomationError);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(1.5, 1.5)]
    [InlineData(2, 2)]
    [InlineData(25, 2)]
    public void Stake_is_clamped_to_one_or_two_dollars(decimal requested, decimal expected)
    {
        Assert.Equal(expected, IPhoneJourneyStrategy.ClampStake(requested));
    }

    [Fact]
    public void Three_losses_in_a_row_pause_for_fifteen_minutes_and_a_win_resets_the_count()
    {
        var user = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        IPhoneJourneyStrategy.Reset(user);

        IPhoneJourneyStrategy.RecordOutcome(user, won: false, t0);
        IPhoneJourneyStrategy.RecordOutcome(user, won: false, t0);
        IPhoneJourneyStrategy.RecordOutcome(user, won: true, t0);      // resets
        IPhoneJourneyStrategy.RecordOutcome(user, won: false, t0);
        IPhoneJourneyStrategy.RecordOutcome(user, won: false, t0);
        Assert.False(IPhoneJourneyStrategy.IsPaused(user, t0, out _));

        IPhoneJourneyStrategy.RecordOutcome(user, won: null, t0);      // a tie changes nothing
        Assert.False(IPhoneJourneyStrategy.IsPaused(user, t0, out _));

        IPhoneJourneyStrategy.RecordOutcome(user, won: false, t0);     // third loss
        Assert.True(IPhoneJourneyStrategy.IsPaused(user, t0.AddMinutes(14), out var until));
        Assert.Equal(t0 + IPhoneJourneyStrategy.PauseLength, until);
        Assert.False(IPhoneJourneyStrategy.IsPaused(user, t0.AddMinutes(15).AddSeconds(1), out _));

        IPhoneJourneyStrategy.Reset(user);
    }

    [Fact]
    public void The_id_is_recognised_case_insensitively_and_nothing_else_matches()
    {
        Assert.True(IPhoneJourneyStrategy.Is("iphone_journey"));
        Assert.True(IPhoneJourneyStrategy.Is(" IPHONE_JOURNEY "));
        Assert.False(IPhoneJourneyStrategy.Is("rsi"));
        Assert.False(IPhoneJourneyStrategy.Is(null));
    }
}
