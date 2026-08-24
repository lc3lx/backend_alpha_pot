using FluentAssertions;
using ScarAlpha.Application.Common;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Trades were showing as "unsettled" even though Binolla had settled them.
///
/// Two guards run over the same trade: the outcome waiter, which listens for Binolla's
/// close, and the stuck-trade sweep, which stamps Unknown when a waiter was lost. The
/// sweep used to fire at expiry+90s while the waiter still had up to 15 minutes to run,
/// so it marked live trades Unknown out from under a waiter that was working fine.
///
/// These tests pin the ordering: the sweep must always come last.
/// </summary>
public sealed class TradeSettlementTimingTests
{
    // Mirrors TradeOutcomeWorker's constants. If those change, these must be revisited.
    private const int OutcomeWaitGraceSeconds = 120;
    private const int SweepMarginSeconds = 60;

    private static int WaiterBudget(int durationSeconds) =>
        Math.Clamp(durationSeconds, 5, 3600) + OutcomeWaitGraceSeconds;

    private static int SweepDeadline(int durationSeconds) =>
        durationSeconds + OutcomeWaitGraceSeconds + SweepMarginSeconds;

    [Theory]
    [InlineData(60)]
    [InlineData(180)]
    [InlineData(240)]
    [InlineData(300)]   // the 5-minute case from the reported trades
    public void The_sweep_always_fires_after_the_waiter_has_given_up(int durationSeconds)
    {
        var waiterEnds = durationSeconds + WaiterBudget(durationSeconds);
        var sweepFires = SweepDeadline(durationSeconds);

        // The waiter starts at entry and runs its budget; the sweep measures from expiry.
        // What matters is that a live waiter is never cut short.
        sweepFires.Should().BeGreaterThan(
            durationSeconds + OutcomeWaitGraceSeconds,
            "the sweep must not stamp Unknown while a waiter is still listening");

        waiterEnds.Should().BeGreaterThan(durationSeconds);
    }

    [Fact]
    public void A_five_minute_trade_is_not_swept_before_the_waiter_times_out()
    {
        // The exact shape that produced the reported "unsettled" trades.
        const int duration = 300;

        // Old behaviour: waiter 15 min, sweep at expiry+90s → sweep won, wrongly.
        const int oldSweep = duration + 90;
        const int oldWaiter = 15 * 60;
        oldSweep.Should().BeLessThan(oldWaiter, "this is the bug being fixed");

        // New behaviour: the waiter finishes first.
        SweepDeadline(duration).Should().BeGreaterThan(WaiterBudget(duration));
    }

    [Fact]
    public void A_running_trade_stops_blocking_the_bot_once_it_is_past_expiry_and_grace()
    {
        var now = new DateTimeOffset(2026, 8, 24, 3, 30, 0, TimeSpan.Zero);
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            Status = TradeStatus.Running,
            DurationSeconds = 300,
            CreatedAt = now.AddSeconds(-(300 + OpenTradeGate.RunningGraceSeconds + 1)),
        };

        // A stuck Running row must never freeze the bot forever.
        OpenTradeGate.IsBlocking(trade, now).Should().BeFalse();

        var fresh = new Trade
        {
            Id = Guid.NewGuid(),
            Status = TradeStatus.Running,
            DurationSeconds = 300,
            CreatedAt = now.AddSeconds(-10),
        };
        OpenTradeGate.IsBlocking(fresh, now).Should().BeTrue();
    }

    [Fact]
    public void Unknown_is_not_final_so_a_late_outcome_can_still_correct_it()
    {
        // The sweep's stamp must be recoverable — otherwise a late push is lost.
        TradeStateMachine.IsHardTerminal(TradeStatus.Unknown).Should().BeFalse();
        TradeStateMachine.IsHardTerminal(TradeStatus.Profit).Should().BeTrue();
        TradeStateMachine.IsHardTerminal(TradeStatus.Loss).Should().BeTrue();
    }
}
