using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Infrastructure.Strategies;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class Phase5RsiTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Asset = "EURUSD_otc";
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wilder_rsi_handles_flat_gain_and_loss_series()
    {
        var calculator = new RsiCalculator();
        var options = RsiStrategyOptions.Default60Seconds;

        calculator.CalculateRsi(Enumerable.Repeat(100m, 15).ToList(), options).Should().Be(50m);
        calculator.CalculateRsi(Enumerable.Range(0, 15).Select(i => 100m + i).ToList(), options).Should().Be(100m);
        calculator.CalculateRsi(Enumerable.Range(0, 15).Select(i => 100m - i).ToList(), options).Should().Be(0m);
    }

    [Fact]
    public async Task Insufficient_completed_candles_throw()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var candles = CreateCandles(Enumerable.Range(0, 20).Select(i => 100m - i).ToList());

        var act = () => service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);
        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Closed_oversold_without_zone_respect_does_not_enter()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var candles = CreateCandles(DumpCloses(26));

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Rsi.Should().Be(0m);
        signal.Signal.Should().Be("None");
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.TotalSignals.Should().Be(0);
    }

    [Fact]
    public async Task Closed_overbought_without_zone_respect_does_not_enter()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var candles = CreateCandles(RallyCloses(26));

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Rsi.Should().Be(100m);
        signal.Signal.Should().Be("None");
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.TotalSignals.Should().Be(0);
    }

    [Fact]
    public async Task Failed_historical_filter_suppresses_the_current_signal()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var candles = CreateCandles(DumpCloses(26));

        var signal = await service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);

        signal.Signal.Should().Be("None");
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.MinimumSuccessRate.Should().Be(75m);
    }

    [Fact]
    public async Task Forming_bar_leaving_zone_does_not_enter_on_closed_rsi()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var closes = OversoldWithRespectedBounce();
        closes.Add(110m);
        var candles = CreateCandles(closes, openLastIndex: closes.Count - 1);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("None");
        signal.LiveRsi.Should().NotBeNull();
        signal.LiveRsi!.Value.Should().BeGreaterThan(options.Oversold);
    }

    [Fact]
    public async Task Live_forming_bar_at_extreme_enters_without_waiting_for_close()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var closes = OversoldWithRespectedBounce();
        closes.Add(closes[^1] - 2m);
        var candles = CreateCandles(closes, openLastIndex: closes.Count - 1);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("Call");
        signal.CandleTime.Should().Be(candles[^1].Timestamp);
        signal.AutomationError.Should().BeNull();
        signal.Backtest!.Passed.Should().BeTrue();
        signal.LiveRsi.Should().NotBeNull();
        signal.LiveRsi!.Value.Should().BeLessThanOrEqualTo(options.Oversold);
    }

    [Fact]
    public async Task Null_EndTimestamp_candles_are_excluded_as_not_closed()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var start = Now - TimeSpan.FromMinutes(25);
        var candles = Enumerable.Range(0, 26)
            .Select(i => new RsiCandle(start.AddMinutes(i), 100m - i, EndTimestamp: null))
            .ToList();

        var act = () => service.GetSignalAsync(UserId, Asset, candles, options, Now);
        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Analyze_twice_still_emits_until_MarkSignalEmitted()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var candles = CreateCandles(OversoldWithRespectedBounce());

        var first = await service.GetSignalAsync(UserId, Asset, candles, options, Now);
        var second = await service.GetSignalAsync(UserId, Asset, candles, options, Now);
        first.Signal.Should().Be("Call");
        second.Signal.Should().Be("Call");

        service.MarkSignalEmitted(UserId, Asset, options.TimeframeSeconds, first.CandleTime);

        var third = await service.GetSignalAsync(UserId, Asset, candles, options, Now);
        third.Signal.Should().Be("None");
        third.Backtest!.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Respected_zone_passes_and_dump_without_exit_fails()
    {
        var service = new RsiSignalService(new RsiCalculator());

        var respected = await service.GetSignalAsync(
            UserId, Asset, CreateCandles(OversoldWithRespectedBounce()),
            RsiStrategyOptions.Default60Seconds, Now);
        respected.Backtest!.Passed.Should().BeTrue();
        respected.Signal.Should().Be("Call");

        var dump = await service.GetSignalAsync(
            UserId, Asset, CreateCandles(DumpCloses(26)),
            RsiStrategyOptions.Default60Seconds, Now);
        dump.Backtest!.Passed.Should().BeFalse();
        dump.Signal.Should().Be("None");
        dump.Backtest.SuccessRate.Should().Be(0m);
    }

    [Fact]
    public async Task Mid_range_rsi_still_runs_backtest_but_emits_none()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        // Flat series → RSI 50, between 25 and 75.
        var candles = CreateCandles(Enumerable.Repeat(100m, 40).ToList());

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().Be(50m);
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.LookbackCandles.Should().Be(200);
    }

    [Fact]
    public async Task Below_minimum_success_rate_emits_none()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var candles = CreateCandles(DumpCloses(26));
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 75m };

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("None");
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.SuccessRate.Should().BeLessThan(75m);
    }

    [Fact]
    public void FromBotDurationSeconds_maps_3_4_5_minutes()
    {
        RsiStrategyOptions.FromBotDurationSeconds(180).ExpiryCandles.Should().Be(3);
        RsiStrategyOptions.FromBotDurationSeconds(240).ExpiryCandles.Should().Be(4);
        RsiStrategyOptions.FromBotDurationSeconds(300).ExpiryCandles.Should().Be(5);
        RsiStrategyOptions.FromBotDurationSeconds(60).ExpiryCandles.Should().Be(5);
    }

    [Fact]
    public async Task Live_rsi_at_extreme_enters_even_after_closed_candle_lag()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MaxEntryLagSeconds = 20 };
        var candles = CreateCandles(OversoldWithRespectedBounce());
        var lateNow = Now.AddMinutes(5);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, lateNow);

        signal.Signal.Should().Be("Call");
        signal.AutomationError.Should().BeNull();
        signal.Backtest!.Passed.Should().BeTrue();
        signal.LiveRsi.Should().NotBeNull();
        signal.LiveRsi!.Value.Should().BeLessThanOrEqualTo(options.Oversold);
    }

    [Fact]
    public async Task Zone_touch_then_bounce_passes_call_backtest()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var signal = await service.GetSignalAsync(UserId, Asset, CreateCandles(OversoldWithRespectedBounce()), options, Now);

        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.LookbackCandles.Should().Be(200);
        signal.Backtest.TotalSignals.Should().BeGreaterThan(0);
        signal.Backtest.Passed.Should().BeTrue();
        signal.Backtest.SuccessRate.Should().BeGreaterThanOrEqualTo(75m);
    }

    [Fact]
    public async Task Live_overbought_with_put_backtest_enters_put()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var signal = await service.GetSignalAsync(
            UserId, Asset, CreateCandles(OverboughtWithRespectedDrop()), options, Now);

        signal.Signal.Should().Be("Put");
        signal.Backtest!.Passed.Should().BeTrue();
        signal.LiveRsi.Should().NotBeNull();
        signal.LiveRsi!.Value.Should().BeGreaterThanOrEqualTo(options.Overbought);
    }

    [Fact]
    public async Task One_minute_is_the_only_supported_timeframe()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { TimeframeSeconds = 300 };
        var candles = CreateCandles(DumpCloses(26));

        var act = () => service.GetSignalAsync(UserId, Asset, candles, options, Now);
        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    private static List<decimal> DumpCloses(int n) =>
        Enumerable.Range(0, n).Select(i => 100m - i).ToList();

    private static List<decimal> RallyCloses(int n) =>
        Enumerable.Range(0, n).Select(i => 100m + i).ToList();

    /// <summary>Touch RSI 25, bounce out of the zone, then return to oversold.</summary>
    private static List<decimal> OversoldWithRespectedBounce()
    {
        var closes = new List<decimal>();
        for (var i = 0; i < 16; i++)
            closes.Add(80m - i * 3);
        for (var i = 1; i <= 12; i++)
            closes.Add(35m + i * 5);
        for (var i = 1; i <= 16; i++)
            closes.Add(95m - i * 4);
        return closes;
    }

    /// <summary>Touch RSI 75, drop out of the zone, then return to overbought.</summary>
    private static List<decimal> OverboughtWithRespectedDrop()
    {
        var closes = new List<decimal>();
        for (var i = 0; i < 16; i++)
            closes.Add(20m + i * 3);
        for (var i = 1; i <= 12; i++)
            closes.Add(65m - i * 5);
        for (var i = 1; i <= 16; i++)
            closes.Add(5m + i * 4);
        return closes;
    }

    private static List<RsiCandle> CreateCandles(List<decimal> closes, int? openLastIndex = null)
    {
        var start = Now - TimeSpan.FromMinutes(closes.Count - 1);
        return closes.Select((close, index) => new RsiCandle(
            start.AddMinutes(index),
            close,
            openLastIndex == index ? Now.AddSeconds(10) : Now.AddSeconds(-1))).ToList();
    }
}
