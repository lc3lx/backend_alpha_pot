using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Infrastructure.Strategies;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The calibration offset aligns the bot's RSI with what the broker chart displays.
///
/// It is an alignment, not a correction: the indicator maths is pinned against an
/// independent reference elsewhere in this suite, so a persistent gap comes from the
/// broker computing RSI differently. These tests hold the calibration to the properties
/// that keep it safe — it never escapes the 0-100 range, it never touches the raw maths,
/// and it moves the entry levels in the direction the user actually sees.
/// </summary>
public sealed class RsiCalibrationTests
{
    /// <summary>Start from plain defaults, whatever ran before this class.</summary>
    public RsiCalibrationTests() => StrategyDefaults.Reset();

    private static IDisposable Pinned(decimal offset)
    {
        var previous = Indicators.RsiCalibrationOffset;
        Indicators.RsiCalibrationOffset = offset;
        return new Restore(() => Indicators.RsiCalibrationOffset = previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }

    private static List<decimal> Rising(int count) =>
        Enumerable.Range(0, count).Select(i => 100m + i * 0.3m - (i % 3) * 0.1m).ToList();

    [Fact]
    public void Calibration_shifts_the_reading_down_by_exactly_the_offset()
    {
        var closes = Rising(250);
        var raw = Indicators.RawRsiSeries(closes, 14)[^1];

        using var _ = Pinned(6m);

        Indicators.Rsi(closes, 14)!.Value.Should().Be(raw - 6m);
    }

    [Fact]
    public void Zero_offset_leaves_every_value_untouched()
    {
        var closes = Rising(250);
        var raw = Indicators.RawRsiSeries(closes, 14);

        using var _ = Pinned(0m);
        var calibrated = Indicators.RsiSeries(closes, 14);

        calibrated.Should().Equal(raw);
    }

    [Fact]
    public void The_raw_maths_is_never_affected_by_the_calibration()
    {
        var closes = Rising(250);
        var before = Indicators.RawRsiSeries(closes, 14)[^1];

        using var _ = Pinned(20m);

        // The pure indicator has to stay pure — it is what the reference test pins.
        Indicators.RawRsiSeries(closes, 14)[^1].Should().Be(before);
    }

    [Fact]
    public void A_calibrated_value_can_never_leave_the_rsi_range()
    {
        // A monotonic rise pins raw RSI at 100; a monotonic fall pins it at 0.
        var up = Enumerable.Range(0, 250).Select(i => 100m + i).ToList();
        var down = Enumerable.Range(0, 250).Select(i => 500m - i).ToList();

        using var _ = Pinned(25m);

        Indicators.Rsi(up, 14)!.Value.Should().Be(75m);
        // 0 - 25 would be negative; it must clamp instead.
        Indicators.Rsi(down, 14)!.Value.Should().Be(0m);

        using var __ = Pinned(-25m);
        Indicators.Rsi(up, 14)!.Value.Should().Be(100m);
        Indicators.Rsi(down, 14)!.Value.Should().Be(25m);
    }

    [Fact]
    public void An_absurd_offset_is_rejected_rather_than_silently_applied()
    {
        var tooBig = () => Indicators.RsiCalibrationOffset = 40m;
        tooBig.Should().Throw<ArgumentOutOfRangeException>();

        var tooSmall = () => Indicators.RsiCalibrationOffset = -40m;
        tooSmall.Should().Throw<ArgumentOutOfRangeException>();

        Indicators.RsiCalibrationOffset.Should().BeInRange(-25m, 25m);
    }

    [Fact]
    public void Every_rsi_path_sees_the_same_calibrated_value()
    {
        // There used to be three separate Wilder implementations, and the one feeding
        // signals ignored the calibration — so the page could show one number while the
        // entry used another. They must all agree.
        var closes = Rising(250);
        var options = RsiStrategyOptions.Default60Seconds;

        using var _ = Pinned(6m);

        var viaIndicators = Indicators.Rsi(closes, options.Period)!.Value;
        var viaCalculator = new RsiCalculator().CalculateRsi(closes, options);

        viaCalculator.Should().Be(viaIndicators);
        viaCalculator.Should().Be(Indicators.RawRsiSeries(closes, options.Period)[^1] - 6m);
    }

    /// <summary>
    /// The behaviour in one line: with a 6-point calibration and PutMin at 75, a PUT
    /// needs a TRUE reading of 81, and the page shows 75 when it happens.
    /// </summary>
    [Theory]
    [InlineData(81.08, 75.08, true)]    // true 81 -> shown 75 -> enters
    [InlineData(79.94, 73.94, false)]   // true 80 -> shown 74 -> does not
    public void A_put_needs_a_true_reading_of_eighty_one_and_displays_seventy_five(
        double rawApprox, double shownApprox, bool shouldEnter)
    {
        using var _ = Pinned(6m);

        var shown = (decimal)rawApprox - Indicators.RsiCalibrationOffset;

        Math.Round(shown, 2).Should().Be(Math.Round((decimal)shownApprox, 2));
        RsiEntryLevels.IsPutRsi(shown).Should().Be(shouldEnter);
    }

    [Fact]
    public void The_entry_level_moves_by_exactly_the_calibration_for_both_sides()
    {
        using var _ = Pinned(6m);

        // PUT at 75 shown  =>  75 + 6 = 81 true.
        // CALL at 25 shown =>  25 + 6 = 31 true.
        var putTrue = RsiEntryLevels.PutMin + Indicators.RsiCalibrationOffset;
        var callTrue = RsiEntryLevels.CallMax + Indicators.RsiCalibrationOffset;

        putTrue.Should().Be(81m);
        callTrue.Should().Be(31m);

        RsiEntryLevels.IsPutRsi(putTrue - Indicators.RsiCalibrationOffset).Should().BeTrue();
        RsiEntryLevels.IsCallRsi(callTrue - Indicators.RsiCalibrationOffset).Should().BeTrue();
    }

    [Fact]
    public void Calibration_makes_the_bot_reach_the_entry_level_later_not_sooner()
    {
        // The point of the offset: with the bot reading 6 high, it hit 75 while the
        // chart still showed 69 — entering early. Calibrated, both reach 75 together.
        var closes = Rising(250);
        var raw = Indicators.RawRsiSeries(closes, 14)[^1];

        using var _ = Pinned(6m);
        var shown = Indicators.Rsi(closes, 14)!.Value;

        shown.Should().BeLessThan(raw);
        RsiEntryLevels.IsPutRsi(shown).Should().Be(shown >= RsiEntryLevels.PutMin);
    }
}
