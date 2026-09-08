using FluentAssertions;
using ScarAlpha.Application.Common;
using ScarAlpha.Domain.Enums;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The broker is the authority on what a trade paid.
///
/// A settled trade is normally immutable, and that is right — a late duplicate frame must
/// not move a finished result. But it also meant a WRONG result was permanent: a trade
/// that won on Binolla and was recorded as a loss stayed a loss, and the user's history
/// disagreed with their broker account with no way back. These pin the one path that is
/// allowed to fix that, and the fence around it.
/// </summary>
public sealed class TradeCorrectionTests
{
    [Theory]
    [InlineData(TradeStatus.Loss, TradeStatus.Profit)]
    [InlineData(TradeStatus.Profit, TradeStatus.Loss)]
    [InlineData(TradeStatus.Tie, TradeStatus.Profit)]
    [InlineData(TradeStatus.Loss, TradeStatus.Tie)]
    public void A_settled_trade_can_be_corrected_to_what_binolla_says(
        TradeStatus recorded,
        TradeStatus broker)
    {
        var status = recorded;

        TradeStateMachine.TryApplyBrokerCorrection(ref status, broker).Should().BeTrue();
        status.Should().Be(broker);
    }

    [Fact]
    public void The_ordinary_final_path_still_cannot_overwrite_a_settled_trade()
    {
        // This is the fence: only the reconciliation path may correct a finished trade.
        var status = TradeStatus.Loss;

        TradeStateMachine.TryApplyFinalOutcome(ref status, TradeStatus.Profit).Should().BeFalse();
        status.Should().Be(TradeStatus.Loss);
    }

    [Fact]
    public void Agreement_is_not_a_correction()
    {
        var status = TradeStatus.Profit;

        TradeStateMachine.TryApplyBrokerCorrection(ref status, TradeStatus.Profit).Should().BeFalse();
        status.Should().Be(TradeStatus.Profit);
    }

    [Fact]
    public void A_cancelled_trade_is_never_corrected()
    {
        // It never reached the broker, so the broker has nothing to say about it.
        var status = TradeStatus.Cancelled;

        TradeStateMachine.TryApplyBrokerCorrection(ref status, TradeStatus.Profit).Should().BeFalse();
        status.Should().Be(TradeStatus.Cancelled);
    }

    [Theory]
    [InlineData(TradeStatus.Running)]
    [InlineData(TradeStatus.Pending)]
    [InlineData(TradeStatus.Unknown)]
    public void A_non_final_status_is_only_ever_moved_to_a_real_outcome(TradeStatus start)
    {
        var status = start;

        // Correcting to a non-outcome is meaningless and must be refused outright.
        TradeStateMachine.TryApplyBrokerCorrection(ref status, TradeStatus.Running).Should().BeFalse();
        status.Should().Be(start);
    }
}

/// <summary>
/// Which account reads the market for everyone. Market data is identical per account, so
/// one session must be able to serve the whole fleet — 1000 users cannot mean 1000
/// identical passes over the same candles.
/// </summary>
public sealed class AnalysisAccountTests : IDisposable
{
    private readonly Guid? _original = AnalysisAccount.UserId;

    public void Dispose() => AnalysisAccount.UserId = _original ?? Guid.Empty;

    [Fact]
    public void The_dedicated_account_is_tried_first()
    {
        var dedicated = Guid.NewGuid();
        var users = new[] { Guid.NewGuid(), Guid.NewGuid() };
        AnalysisAccount.UserId = dedicated;

        AnalysisAccount.ScanOrder(users).Should().Equal(dedicated, users[0], users[1]);
    }

    [Fact]
    public void The_dedicated_account_is_never_attempted_twice()
    {
        var dedicated = Guid.NewGuid();
        var other = Guid.NewGuid();
        AnalysisAccount.UserId = dedicated;

        // It is also a running bot — it must still appear exactly once.
        AnalysisAccount.ScanOrder(new[] { other, dedicated }).Should().Equal(dedicated, other);
    }

    [Fact]
    public void Without_one_configured_the_fleet_falls_back_to_user_sessions()
    {
        var users = new[] { Guid.NewGuid(), Guid.NewGuid() };
        AnalysisAccount.UserId = Guid.Empty;

        AnalysisAccount.IsConfigured.Should().BeFalse();
        // Trading must not stop because a system account was never set up.
        AnalysisAccount.ScanOrder(users).Should().Equal(users);
    }
}
