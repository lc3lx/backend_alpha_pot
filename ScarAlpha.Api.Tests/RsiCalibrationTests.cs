using FluentAssertions;
using ScarAlpha.Application.Abstractions;
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
