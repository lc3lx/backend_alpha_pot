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
    public void AlignToBinolla_shifts_80_to_74()
    {
        RsiEntryLevels.AlignToBinolla(80m).Should().Be(74m);
        RsiEntryLevels.AlignToBinolla(5m).Should().Be(11m);
        RsiEntryLevels.AlignToBinolla(25m).Should().Be(31m);
        RsiEntryLevels.AlignToBinolla(26m).Should().Be(20m);
        RsiEntryLevels.AlignToBinolla(100m).Should().Be(94m);
    }

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
        var candles = CreateClosedCandles(DumpCloses(26));

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Rsi.Should().Be(RsiEntryLevels.AlignToBinolla(0m));
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
        var candles = CreateClosedCandles(RallyCloses(26));

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Rsi.Should().Be(RsiEntryLevels.AlignToBinolla(100m));
        signal.Signal.Should().Be("None");
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.TotalSignals.Should().Be(0);
    }

    [Fact]
    public async Task Failed_historical_filter_suppresses_the_current_signal()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var candles = CreateClosedCandles(DumpCloses(26));

        var signal = await service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);

        signal.Signal.Should().Be("None");
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.MinimumSuccessRate.Should().Be(75m);
    }

    [Fact]
    public async Task Forming_bar_leaving_zone_does_not_enter_on_live_rsi()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var closes = OversoldWithRespectedBounce();
        closes.Add(110m);
        var candles = CreateCandles(closes, openLastIndex: closes.Count - 1);
        // Mid-minute: any prior closed extreme is outside the 5s post-close window.
        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now.AddSeconds(30));

        signal.Signal.Should().Be("None");
        signal.LiveRsi.Should().NotBeNull();
        signal.LiveRsi!.Value.Should().BeGreaterThan(options.Oversold);
    }

    [Fact]
    public async Task Forming_bar_at_extreme_does_not_enter_before_candle_closes()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var closes = OversoldWithRespectedBounce();
        closes.Add(closes[^1] - 2m);
        var candles = CreateCandles(closes, openLastIndex: closes.Count - 1);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now.AddSeconds(30));

        signal.Signal.Should().Be("None");
        signal.LiveRsi.Should().NotBeNull();
        signal.LiveRsi!.Value.Should().BeLessThanOrEqualTo(options.Oversold);
    }

    [Fact]
    public async Task Closed_bar_at_extreme_enters_right_after_close()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var candles = CreateClosedCandles(OversoldWithRespectedBounce());

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("Call");
        signal.CandleTime.Should().Be(Now);
        signal.AutomationError.Should().BeNull();
        signal.Backtest!.Passed.Should().BeTrue();
        signal.Rsi.Should().BeLessThanOrEqualTo(options.Oversold);
    }

    [Fact]
    public async Task Closed_above_25_after_oversold_touch_does_not_enter_call()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var closes = OversoldWithRespectedBounce();
        // Bounce close back above 25 — same idea as wick-to-25 then close above.
        closes.Add(110m);
        var candles = CreateClosedCandles(closes);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().BeGreaterThan(RsiEntryLevels.CallMax);
        signal.Rsi.Should().BeLessThan(RsiEntryLevels.PutMin);
    }

    [Fact]
    public async Task Too_few_elapsed_1m_bars_are_insufficient()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var candles = Enumerable.Range(0, 10)
            .Select(i => new RsiCandle(Now.AddMinutes(i - 9), 100m - i, EndTimestamp: null))
            .ToList();

        var act = () => service.GetSignalAsync(UserId, Asset, candles, options, Now);
        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Current_minute_last_tick_is_not_a_closed_1m_bar()
    {
        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 30, TimeSpan.Zero);
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var closes = OversoldWithRespectedBounce();
        closes.Add(110m);
        var currentStart = now.AddSeconds(-30);
        var candles = closes.Select((close, index) =>
        {
            var ts = currentStart.AddMinutes(index - (closes.Count - 1));
            return new RsiCandle(ts, close, EndTimestamp: ts.AddSeconds(8));
        }).ToList();

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, now);

        signal.Signal.Should().Be("None");
        signal.LiveRsi.Should().NotBeNull();
        signal.LiveRsi!.Value.Should().BeGreaterThan(options.Oversold);
    }

    [Fact]
    public async Task Analyze_twice_still_emits_until_MarkSignalEmitted()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var candles = CreateClosedCandles(OversoldWithRespectedBounce());

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
            UserId, Asset, CreateClosedCandles(OversoldWithRespectedBounce()),
            RsiStrategyOptions.Default60Seconds, Now);
        respected.Backtest!.Passed.Should().BeTrue();
        respected.Signal.Should().Be("Call");

        var dump = await service.GetSignalAsync(
            UserId, Asset, CreateClosedCandles(DumpCloses(26)),
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
        var candles = CreateClosedCandles(Enumerable.Repeat(100m, 40).ToList());

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().Be(RsiEntryLevels.AlignToBinolla(50m));
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.LookbackCandles.Should().Be(200);
    }

    [Fact]
    public async Task Backtest_alone_does_not_enter_when_closed_rsi_is_between_25_and_75()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var closes = OversoldWithRespectedBounce();
        closes.Add(110m);
        var candles = CreateClosedCandles(closes);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().BeGreaterThan(RsiEntryLevels.CallMax);
        signal.Rsi.Should().BeLessThan(RsiEntryLevels.PutMin);
    }

    [Fact]
    public async Task Below_minimum_success_rate_emits_none()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var candles = CreateClosedCandles(DumpCloses(26));
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
    public async Task First_seconds_after_close_at_extreme_enters()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var candles = CreateClosedCandles(OversoldWithRespectedBounce());
        var justAfterClose = Now.AddSeconds(2);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, justAfterClose);

        signal.Signal.Should().Be("Call");
        signal.AutomationError.Should().BeNull();
        signal.CandleTime.Should().Be(Now);
        signal.Backtest!.Passed.Should().BeTrue();
        signal.Rsi.Should().BeLessThanOrEqualTo(RsiEntryLevels.CallMax);
    }

    [Fact]
    public async Task Setup_expires_after_5_seconds_and_does_not_reenter_same_close()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var candles = CreateClosedCandles(OversoldWithRespectedBounce());

        var first = await service.GetSignalAsync(UserId, Asset, candles, options, Now);
        first.Signal.Should().Be("Call");

        var stillInside = await service.GetSignalAsync(UserId, Asset, candles, options, Now.AddSeconds(4));
        stillInside.Signal.Should().Be("Call");

        var expired = await service.GetSignalAsync(UserId, Asset, candles, options, Now.AddSeconds(6));
        expired.Signal.Should().Be("None");
        expired.AutomationError.Should().Be("SETUP_EXPIRED");
        expired.Backtest!.Passed.Should().BeTrue();

        var stillExpired = await service.GetSignalAsync(UserId, Asset, candles, options, Now.AddSeconds(20));
        stillExpired.Signal.Should().Be("None");
        stillExpired.AutomationError.Should().Be("SETUP_EXPIRED");
    }

    [Fact]
    public async Task New_closed_extreme_after_leaving_zone_is_a_fresh_setup()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var oversold = CreateClosedCandles(OversoldWithRespectedBounce());
        var mid = CreateClosedCandles(Enumerable.Repeat(100m, 40).ToList());

        var first = await service.GetSignalAsync(UserId, Asset, oversold, options, Now);
        first.Signal.Should().Be("Call");

        var left = await service.GetSignalAsync(UserId, Asset, mid, options, Now.AddSeconds(2));
        left.Signal.Should().Be("None");

        var retouch = await service.GetSignalAsync(UserId, Asset, oversold, options, Now.AddSeconds(3));
        retouch.Signal.Should().Be("Call");
        retouch.AutomationError.Should().BeNull();
    }

    [Fact]
    public async Task Zone_touch_then_bounce_passes_call_backtest()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var signal = await service.GetSignalAsync(UserId, Asset, CreateClosedCandles(OversoldWithRespectedBounce()), options, Now);

        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.LookbackCandles.Should().Be(200);
        signal.Backtest.TotalSignals.Should().BeGreaterThan(0);
        signal.Backtest.Passed.Should().BeTrue();
        signal.Backtest.SuccessRate.Should().BeGreaterThanOrEqualTo(75m);
    }

    [Fact]
    public async Task Closed_overbought_with_put_backtest_enters_put()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var closes = OverboughtWithRespectedDrop();
        for (var i = 0; i < 8; i++)
            closes.Add(closes[^1] + 10m);
        var signal = await service.GetSignalAsync(
            UserId, Asset, CreateClosedCandles(closes), options, Now);

        signal.Signal.Should().Be("Put");
        signal.Backtest!.Passed.Should().BeTrue();
        signal.Rsi.Should().BeGreaterThanOrEqualTo(RsiEntryLevels.PutMin);
    }

    [Fact]
    public async Task Put_does_not_enter_when_closed_rsi_is_below_75()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var closes = OverboughtWithRespectedDrop();
        closes.Add(closes[^1] - 40m);
        var candles = CreateClosedCandles(closes);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().BeLessThan(RsiEntryLevels.PutMin);
    }

    [Fact]
    public void Zone_backtest_is_always_200_one_minute_candles()
    {
        var options = RsiStrategyOptions.Default60Seconds with { BacktestCandleCount = 80, MinimumSuccessRate = 10m };
        var respected = RsiZoneBacktest.Evaluate(OversoldWithRespectedBounce(), options);
        respected.Call.LookbackCandles.Should().Be(200);
        respected.Call.Passed.Should().BeTrue();
        respected.Call.TotalSignals.Should().BeGreaterThan(0);
        respected.Call.MinimumSuccessRate.Should().Be(75m);

        var dump = RsiZoneBacktest.Evaluate(DumpCloses(40), options);
        dump.Call.Passed.Should().BeFalse();
        dump.Call.TotalSignals.Should().Be(0);

        var put = RsiZoneBacktest.Evaluate(OverboughtWithRespectedDrop(), options);
        put.Put.Passed.Should().BeTrue();
        put.Put.TotalSignals.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Trade_gate_rejects_mid_closed_rsi_and_expired_setup()
    {
        var okBacktest = new RsiBacktestStats(4, 4, 0, 100m, 200, 5, 75m, true);
        var callOk = new StrategySignal("rsi", Asset, "Call", 20m, Now, "60", okBacktest, LiveRsi: 40m);
        // Live mid-range must not block — closed RSI is the gate.
        RsiEntryLevels.TryValidateForTrade(callOk, Now.AddSeconds(1), out _).Should().BeTrue();

        var noLive = callOk with { LiveRsi = null };
        RsiEntryLevels.TryValidateForTrade(noLive, Now, out _).Should().BeTrue();

        var midClosed = callOk with { Rsi = 40m };
        RsiEntryLevels.TryValidateForTrade(midClosed, Now, out var midCode).Should().BeFalse();
        midCode.Should().Be("RSI_NOT_OVERSOLD");

        var weakBt = callOk with { Backtest = okBacktest with { Passed = false, SuccessRate = 50m, TotalSignals = 2 } };
        RsiEntryLevels.TryValidateForTrade(weakBt, Now, out var btCode).Should().BeFalse();
        btCode.Should().Be("BACKTEST_NOT_PASSED");

        RsiEntryLevels.TryValidateForTrade(callOk, Now.AddSeconds(6), out var expCode).Should().BeFalse();
        expCode.Should().Be("SETUP_EXPIRED");

        var putOk = new StrategySignal("rsi", Asset, "Put", 80m, Now, "60", okBacktest, LiveRsi: 50m);
        RsiEntryLevels.TryValidateForTrade(putOk, Now, out _).Should().BeTrue();
        var putLow = putOk with { Rsi = 74m };
        RsiEntryLevels.TryValidateForTrade(putLow, Now, out var putCode).Should().BeFalse();
        putCode.Should().Be("RSI_NOT_OVERBOUGHT");
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
        for (var i = 1; i <= 8; i++)
            closes.Add(closes[^1] - 6m);
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
        // Default: last bar is still forming (matches live quote overlay in production).
        // Null EndTimestamp stays open for any evaluation `now` (setup TTL tests advance time).
        var formingIndex = openLastIndex ?? closes.Count - 1;
        return closes.Select((close, index) => new RsiCandle(
            start.AddMinutes(index),
            close,
            index == formingIndex ? null : Now.AddSeconds(-1))).ToList();
    }

    /// <summary>All bars closed; the last bar closes exactly at <see cref="Now"/> (entry window open).</summary>
    private static List<RsiCandle> CreateClosedCandles(List<decimal> closes)
    {
        var start = Now - TimeSpan.FromMinutes(closes.Count);
        return closes.Select((close, index) => new RsiCandle(
            start.AddMinutes(index),
            close,
            Now.AddSeconds(-1))).ToList();
    }
}
