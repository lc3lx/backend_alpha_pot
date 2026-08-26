using FluentAssertions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class StakeProgressionTests
{
    [Theory]
    [InlineData(StakeProgression.RedSignalPro, 25, 40, 25)]
    [InlineData(StakeProgression.AlphaMomentum, 25, 25, 37.5)]
    [InlineData(StakeProgression.ScarPrecision, 25, 25, 50)]
    [InlineData(StakeProgression.TrendBreaker, 25, 25, 75)]
    public void Loss_progression_uses_base_or_last_trade(string mode, decimal baseAmount, decimal last, decimal expected)
    {
        StakeProgression.CalculateNextAfterLoss(mode, baseAmount, last).Should().Be(expected);
    }

    [Fact]
    public void Consecutive_alpha_momentum_losses_compound_on_last_stake()
    {
        var first = StakeProgression.CalculateNextAfterLoss(StakeProgression.AlphaMomentum, 25m, 25m);
        first.Should().Be(37.5m);
        StakeProgression.CalculateNextAfterLoss(StakeProgression.AlphaMomentum, 25m, first).Should().Be(56.25m);
    }

    [Fact]
    public void Win_resets_to_base_amount()
    {
        StakeProgression.ResetAfterWin(25m).Should().Be(25m);
    }

    [Fact]
    public void Unknown_mode_defaults_to_red_signal_pro()
    {
        StakeProgression.NormalizeMode(null).Should().Be(StakeProgression.RedSignalPro);
        StakeProgression.CalculateNextAfterLoss("unknown", 30m, 60m).Should().Be(30m);
    }
}
