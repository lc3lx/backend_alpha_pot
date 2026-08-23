using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Infrastructure.Strategies;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Five alternating 5-minute candles, then enter the sixth in the SAME direction as
/// the fifth — i.e. betting the alternation breaks.
/// </summary>
public sealed class AlternatingCandlesTests
{
    private static readonly Guid UserId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private const string Asset = "EURUSD_otc";
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
    private static AlternatingOptions Options => AlternatingOptions.Default;

    [Fact]
    public void Up_down_up_down_up_enters_call()
    {
        // The fifth candle is up, so the sixth is taken up as well.
        var eval = AlternatingCandlesEngine.Evaluate(Bars("UDUDU"), Options);

        eval.Signal.Should().Be("Call");
        eval.LastDirection.Should().Be(CandleDirection.Up);
        eval.SkipReason.Should().BeNull();
    }

    [Fact]
    public void Down_up_down_up_down_enters_put()
    {
        var eval = AlternatingCandlesEngine.Evaluate(Bars("DUDUD"), Options);

        eval.Signal.Should().Be("Put");
        eval.LastDirection.Should().Be(CandleDirection.Down);
    }

    [Theory]
    [InlineData("UUDUD")]   // repeat at the start
    [InlineData("UDUUD")]   // repeat in the middle
    [InlineData("UDUDD")]   // repeat at the end
    [InlineData("UUUUU")]   // no alternation at all
    public void Any_repeat_breaks_the_pattern(string shape)
    {
        var eval = AlternatingCandlesEngine.Evaluate(Bars(shape), Options);

        eval.Signal.Should().Be("None");
        eval.SkipReason.Should().Be(AlternatingCandlesEngine.ReasonNotAlternating);
    }

    [Fact]
    public void A_flat_candle_breaks_the_pattern_rather_than_picking_a_side()
    {
        // close == open has no direction; counting it either way would invent one.
        var eval = AlternatingCandlesEngine.Evaluate(Bars("UDFDU"), Options);

        eval.Signal.Should().Be("None");
        eval.SkipReason.Should().Be(AlternatingCandlesEngine.ReasonFlatCandle);
    }

    [Fact]
    public void Only_the_last_five_candles_matter()
    {
        // Earlier noise must not disqualify a clean tail.
        var eval = AlternatingCandlesEngine.Evaluate(Bars("UUUUUUUDUDU"), Options);

        eval.Signal.Should().Be("Call");
        eval.Pattern.Should().HaveCount(5);
    }

    [Fact]
    public void A_candle_with_no_open_has_no_direction()
    {
        // The forming bar built from a single live quote carries no open.
        var noOpen = new RsiCandle(Now, 100m, Now.AddMinutes(5));

        AlternatingCandlesEngine.DirectionOf(noOpen).Should().Be(CandleDirection.Flat);
    }

    [Fact]
    public void Too_few_candles_reports_insufficient_data()
    {
        var eval = AlternatingCandlesEngine.Evaluate(Bars("UDU"), Options);

        eval.Signal.Should().Be("None");
        eval.SkipReason.Should().Be(AlternatingCandlesEngine.ReasonInsufficientData);
    }

    [Fact]
    public void The_trade_lasts_one_five_minute_candle()
    {
        Options.TimeframeSeconds.Should().Be(300);
        Options.ExpiryCandles.Should().Be(1);
        Options.DurationSeconds.Should().Be(300);
        StrategyTimeframes.For("alt5").Should().Be(300);
        StrategyTimeframes.For("rsi").Should().Be(60);
    }

    // ---- through the service, including entry discipline ----

    [Fact]
    public async Task Service_emits_call_right_after_the_pattern_candle_closes()
    {
        var service = new AlternatingSignalService();

        var signal = await service.GetSignalAsync(UserId, Asset, ClosedBars("UDUDU"), Options, Now);

        signal.Signal.Should().Be("Call");
        signal.StrategyId.Should().Be("alt5");
        signal.Timeframe.Should().Be("300");
        signal.CandleTime.Should().Be(Now);
        signal.Backtest!.ExpiryCandles.Should().Be(1);
    }

    [Fact]
    public async Task The_setup_expires_once_the_entry_window_passes()
    {
        var service = new AlternatingSignalService();
        var bars = ClosedBars("UDUDU");

        (await service.GetSignalAsync(UserId, Asset, bars, Options, Now)).Signal.Should().Be("Call");
        (await service.GetSignalAsync(UserId, Asset, bars, Options, Now.AddSeconds(4))).Signal.Should().Be("Call");

        var expired = await service.GetSignalAsync(UserId, Asset, bars, Options, Now.AddSeconds(6));
        expired.Signal.Should().Be("None");
        expired.AutomationError.Should().Be("SETUP_EXPIRED");
    }

    [Fact]
    public async Task One_pattern_cannot_open_two_trades()
    {
        var service = new AlternatingSignalService();
        var bars = ClosedBars("UDUDU");

        var first = await service.GetSignalAsync(UserId, Asset, bars, Options, Now);
        first.Signal.Should().Be("Call");

        service.MarkSignalEmitted(UserId, Asset, 300, first.CandleTime);

        var second = await service.GetSignalAsync(UserId, Asset, bars, Options, Now);
        second.Signal.Should().Be("None");
        second.AutomationError.Should().Be("SETUP_CONSUMED");
    }

    [Fact]
    public async Task A_forming_candle_cannot_complete_the_pattern()
    {
        var service = new AlternatingSignalService();
        // The final 'U' would complete UDUDU — but it is still forming, so the closed
        // tail is UUUDUD, whose last five repeat at the start and do not alternate.
        const string shape = "UUUDUDU";
        var start = Now - TimeSpan.FromMinutes(5 * (shape.Length - 1));
        var bars = shape
            .Select((ch, i) => Candle(start.AddMinutes(5 * i), ch))
            .ToList();

        // Mid-candle: the last bar has not closed yet.
        var signal = await service.GetSignalAsync(
            UserId, Asset, bars, Options, Now.AddSeconds(120));

        signal.Signal.Should().Be("None");
    }

    [Fact]
    public async Task Only_the_five_minute_timeframe_is_accepted()
    {
        var service = new AlternatingSignalService();
        var act = () => service.GetSignalAsync(
            UserId, Asset, ClosedBars("UDUDU"), Options with { TimeframeSeconds = 60 }, Now);

        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    // ---- fixtures ----

    /// <summary>'U' up, 'D' down, 'F' flat. Bars are 5 minutes apart.</summary>
    private static List<RsiCandle> Bars(string shape)
    {
        var start = Now - TimeSpan.FromMinutes(5 * shape.Length);
        return shape.Select((ch, i) => Candle(start.AddMinutes(5 * i), ch)).ToList();
    }

    /// <summary>All bars closed, the last closing exactly at <see cref="Now"/>.</summary>
    private static List<RsiCandle> ClosedBars(string shape)
    {
        // One extra leading bar so the service's minimum is met.
        var padded = "U" + shape;
        var start = Now - TimeSpan.FromMinutes(5 * padded.Length);
        return padded.Select((ch, i) => Candle(start.AddMinutes(5 * i), ch)).ToList();
    }

    private static RsiCandle Candle(DateTimeOffset start, char shape)
    {
        const decimal open = 100m;
        var close = shape switch
        {
            'U' => 101m,
            'D' => 99m,
            _ => open,          // flat
        };
        return new RsiCandle(
            Timestamp: start,
            Close: close,
            EndTimestamp: start.AddMinutes(5),
            High: Math.Max(open, close) + 0.5m,
            Low: Math.Min(open, close) - 0.5m,
            Open: open);
    }
}
