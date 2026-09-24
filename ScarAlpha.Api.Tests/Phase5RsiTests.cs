using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Infrastructure.Strategies;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class Phase5RsiTests
{
    /// <summary>Start from plain defaults, whatever ran before this class.</summary>
    public Phase5RsiTests() => StrategyDefaults.Reset();

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Asset = "EURUSD_otc";
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rsi_matches_the_reference_wilder_value_with_no_broker_offset()
    {
        // Wilder's own worked example (New Concepts in Technical Trading Systems).
        // Any constant "alignment" offset would break this.
        var closes = new[]
        {
            44.34m, 44.09m, 44.15m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m,
            45.84m, 46.08m, 45.89m, 46.03m, 45.61m, 46.28m, 46.28m, 46.00m,
            46.03m, 46.41m, 46.22m, 45.64m
        };

        // RawRsiSeries deliberately: this pins the indicator itself, so a broker
        // calibration can never quietly move the maths out from under it.
        var raw = Indicators.RawRsiSeries(closes, 14);

        Math.Round(raw[^1], 3).Should().Be(57.915m);

        // The seeding bar is the textbook ~70.5 first value.
        Math.Round(raw[14], 2).Should().Be(70.46m);
    }

    [Fact]
    public void Short_history_and_long_history_disagree_which_is_why_warmup_is_enforced()
    {
        // Same tail, different start. Wilder RSI is recursive, so the value drifts
        // until the seed washes out — this is the real cause of "bot 78 / platform 71",
        // and it is why no constant offset can fix it.
        var full = Enumerable.Range(0, 400)
            .Select(i => 100m + (i % 7) - (i % 5) * 0.5m)
            .ToList();
        var truncated = full.Skip(full.Count - 20).ToList();

        var fromFull = Indicators.RawRsiSeries(full, 14)[^1];
        var fromShort = Indicators.RawRsiSeries(truncated, 14)[^1];

        Math.Abs(fromFull - fromShort).Should().BeGreaterThan(1m);

        // Past the warmup floor the two agree to within a rounding step.
        var warm = full.Skip(full.Count - IndicatorWarmup.ForRsi(14)).ToList();
        Math.Abs(fromFull - Indicators.RawRsiSeries(warm, 14)[^1]).Should().BeLessThan(0.01m);
    }

    [Fact]
    public async Task Below_warmup_floor_emits_no_rsi_instead_of_a_wrong_one()
    {
        var service = new RsiSignalService(new RsiCalculator());
        // Comfortably past the old Period+Expiry+2 floor, still short of chart accuracy.
        var candles = CreateClosedCandles(WithWarmup(DumpCloses(20), bars: 60));

        var act = () => service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);
        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Time_gaps_are_trimmed_so_rsi_never_spans_a_missing_minute()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var closes = OversoldWithRespectedBounce();
        var candles = CreateClosedCandles(closes);

        // Punch a gap exceeding MaxFillGapSeconds (2 hours) before the last 10 bars: the contiguous tail is then too short to trust.
        for (var i = 0; i < candles.Count - 10; i++)
        {
            candles[i] = candles[i] with { Timestamp = candles[i].Timestamp.AddHours(-10) };
        }

        var act = () => service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);
        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
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
        var candles = CreateClosedCandles(DumpCloses(200));

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Rsi.Should().Be(0m);
        signal.Signal.Should().Be("None");
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.SuccessRate.Should().Be(0m);
    }

    [Fact]
    public async Task Closed_overbought_without_zone_respect_does_not_enter()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var candles = CreateClosedCandles(RallyCloses(200));

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Rsi.Should().Be(100m);
        signal.Signal.Should().Be("None");
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.SuccessRate.Should().Be(0m);
    }

    [Fact]
    public async Task Failed_historical_filter_suppresses_the_current_signal()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var candles = CreateClosedCandles(DumpCloses(200));

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
        closes.Add(closes[^1] + 40m);
        var candles = CreateCandles(closes, openLastIndex: closes.Count - 1);
        // Well past the entry window, so any prior closed extreme has expired.
        // 30s in: the last bar is still forming, and the previous close is past the window.
        using var _ = PinnedEntryWindow(5);
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

        using var _ = PinnedEntryWindow(5);
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
    public async Task Closed_above_the_call_level_after_a_touch_does_not_enter()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var closes = OversoldWithRespectedBounce();
        // Bounce close back above 25 — same idea as wick-to-25 then close above.
        for (var i = 0; i < 4; i++) closes.Add(closes[^1] + 15m);
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
        closes.Add(closes[^1] + 40m);
        var currentStart = now.AddSeconds(-30);
        var candles = closes.Select((close, index) =>
        {
            var ts = currentStart.AddMinutes(index - (closes.Count - 1));
            return new RsiCandle(ts, close, EndTimestamp: ts.AddSeconds(8));
        }).ToList();

        using var _ = PinnedEntryWindow(5);
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
            UserId, Asset, CreateClosedCandles(DumpCloses(200)),
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
        var candles = CreateClosedCandles(Enumerable.Repeat(100m, 220).ToList());

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().Be(50m);
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.LookbackCandles.Should().Be(200);
    }

    [Fact]
    public async Task Backtest_alone_does_not_enter_when_closed_rsi_is_mid_range()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var closes = OversoldWithRespectedBounce();
        for (var i = 0; i < 4; i++) closes.Add(closes[^1] + 15m);
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
        var candles = CreateClosedCandles(DumpCloses(200));
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
    public void Trade_duration_is_five_minutes_when_the_bot_stored_one_minute()
    {
        RsiStrategyOptions.TradeDurationSeconds(60).Should().Be(300);
        RsiStrategyOptions.TradeDurationSeconds(60, 5).Should().Be(300);
        RsiStrategyOptions.TradeDurationSeconds(180).Should().Be(180);
        RsiStrategyOptions.TradeDurationSeconds(300).Should().Be(300);
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
    public async Task Setup_expires_after_the_entry_window_and_does_not_reenter_same_close()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var candles = CreateClosedCandles(OversoldWithRespectedBounce());

        var first = await service.GetSignalAsync(UserId, Asset, candles, options, Now);
        first.Signal.Should().Be("Call");

        var stillInside = await service.GetSignalAsync(
            UserId, Asset, candles, options, Now.AddSeconds(RsiEntryLevels.SetupTtlSeconds - 1));
        stillInside.Signal.Should().Be("Call");

        var expired = await service.GetSignalAsync(
            UserId, Asset, candles, options, Now.AddSeconds(RsiEntryLevels.SetupTtlSeconds + 1));
        expired.Signal.Should().Be("None");
        expired.AutomationError.Should().Be("SETUP_EXPIRED");
        expired.Backtest!.Passed.Should().BeTrue();

        var stillExpired = await service.GetSignalAsync(
            UserId, Asset, candles, options, Now.AddSeconds(RsiEntryLevels.SetupTtlSeconds + 15));
        stillExpired.Signal.Should().Be("None");
        stillExpired.AutomationError.Should().Be("SETUP_EXPIRED");
    }

    [Fact]
    public async Task New_closed_extreme_after_leaving_zone_is_a_fresh_setup()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds;
        var oversold = CreateClosedCandles(OversoldWithRespectedBounce());
        var mid = CreateClosedCandles(Enumerable.Repeat(100m, 220).ToList());

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
        var signal = await service.GetSignalAsync(
            UserId, Asset, CreateClosedCandles(closes), options, Now);

        signal.Signal.Should().Be("Put");
        signal.Backtest!.Passed.Should().BeTrue();
        signal.Rsi.Should().BeGreaterThanOrEqualTo(RsiEntryLevels.PutMin);
    }

    [Fact]
    public async Task Put_does_not_enter_when_closed_rsi_is_below_the_put_level()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var closes = OverboughtWithRespectedDrop();
        closes.Add(closes[^1] - 40m);
        var candles = CreateClosedCandles(closes);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().BeLessThan(RsiEntryLevels.PutMin);
        // Mid-range still carries both sides so they are ready the moment RSI next touches.
        signal.CallBacktest.Should().NotBeNull();
        signal.PutBacktest.Should().NotBeNull();
        signal.Backtest.Should().NotBeNull();
    }

    [Fact]
    public async Task Cached_backtest_is_used_the_instant_closed_rsi_hits_the_level()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var call = new RsiBacktestStats(4, 4, 0, 100m, 400, 5, 75m, true);
        var put = new RsiBacktestStats(3, 3, 0, 100m, 400, 5, 75m, true);
        var candles = CreateClosedCandles(OversoldWithRespectedBounce());

        var signal = await service.GetSignalAsync(
            UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now,
            cachedCallBacktest: call,
            cachedPutBacktest: put);

        signal.Signal.Should().Be("Call");
        signal.CallBacktest.Should().BeSameAs(call);
        signal.PutBacktest.Should().BeSameAs(put);
        signal.Backtest.Should().BeSameAs(call);
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
        dump.Call.SuccessRate.Should().Be(0m);

        var put = RsiZoneBacktest.Evaluate(OverboughtWithRespectedDrop(), options);
        put.Put.Passed.Should().BeTrue();
        put.Put.TotalSignals.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DisplayFor_picks_the_touch_side_and_the_nearer_side_while_waiting()
    {
        var call = new RsiBacktestStats(2, 2, 0, 100m, 400, 5, 75m, true);
        var put = new RsiBacktestStats(3, 3, 0, 100m, 400, 5, 75m, true);
        RsiZoneBacktest.DisplayFor(20m, call, put).Should().BeSameAs(call);
        RsiZoneBacktest.DisplayFor(80m, call, put).Should().BeSameAs(put);
        RsiZoneBacktest.DisplayFor(40m, call, put).Should().BeSameAs(call);
        RsiZoneBacktest.DisplayFor(60m, call, put).Should().BeSameAs(put);

        var empty = new RsiBacktestStats(0, 0, 0, 0m, 400, 5, 75m, false);
        RsiZoneBacktest.DisplayFor(40m, empty, put).Should().BeSameAs(put);
        RsiZoneBacktest.DisplayFor(60m, call, empty).Should().BeSameAs(call);
        RsiZoneBacktest.DisplayFor(40m, empty, empty).Should().BeSameAs(empty);
    }

    [Fact]
    public void Trade_gate_rejects_mid_closed_rsi_and_expired_setup()
    {
        var okBacktest = new RsiBacktestStats(4, 4, 0, 100m, 400, 5, 75m, true);
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

        RsiEntryLevels.TryValidateForTrade(
            callOk, Now.AddSeconds(RsiEntryLevels.SetupTtlSeconds + 1), out var expCode).Should().BeFalse();
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

    /// <summary>
    /// Pins the entry window for one test and restores it afterwards, so tests about the
    /// FORMING bar do not have to move the clock past a whole minute to beat the window.
    /// </summary>
    private static IDisposable PinnedEntryWindow(int seconds)
    {
        var previous = RsiEntryLevels.SetupTtlSeconds;
        RsiEntryLevels.SetupTtlSeconds = seconds;
        return new Restore(() => RsiEntryLevels.SetupTtlSeconds = previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }

    /// <summary>Bars of neutral history prepended so RSI is past <see cref="IndicatorWarmup"/>.</summary>
    private const int WarmupBars = 170;

    /// <summary>
    /// Wilder RSI is recursive, so a fixture must carry the same warmup the live path
    /// requires. A tight zigzag holds pre-pattern RSI at 50 (no zone visits, so the
    /// backtest still only counts the pattern below) and lets the pattern drive the extreme.
    /// </summary>
    private static List<decimal> WithWarmup(List<decimal> pattern, int bars = WarmupBars)
    {
        var anchor = pattern[0];
        var series = new List<decimal>(bars + pattern.Count);
        for (var i = 0; i < bars; i++)
            series.Add(anchor + (i % 2 == 0 ? 0.5m : -0.5m));
        series.AddRange(pattern);
        return series;
    }

    private static List<decimal> DumpCloses(int n) =>
        Enumerable.Range(0, n).Select(i => 500m - i).ToList();

    private static List<decimal> RallyCloses(int n) =>
        Enumerable.Range(0, n).Select(i => 100m + i).ToList();

    /// <summary>
    /// <summary>
    /// Dip that crosses 25 on the last down bar, bounces back out ABOVE the touch
    /// price after 5 candles (so the 5m trade wins), then slides back into oversold for
    /// the current bar.
    /// </summary>
    private static List<decimal> OversoldWithRespectedBounce()
    {
        var closes = new List<decimal> { 100m };
        for (var visit = 0; visit < 2; visit++)
        {
            for (var i = 0; i < 4; i++) closes.Add(closes[^1] - 10m);  // dip through the level to RSI <= 25
            for (var i = 0; i < 7; i++) closes.Add(closes[^1] + 15m);  // bounce up strongly so price at i+5 is much higher
            for (var i = 0; i < 15; i++) closes.Add(closes[^1] + (i % 2 == 0 ? 0.3m : -0.3m)); // neutral recovery
        }

        for (var i = 0; i < 8; i++) closes.Add(closes[^1] - 15m);      // close back inside oversold <= 25
        return WithWarmup(closes);
    }

    /// <summary>
    /// Mirror of <see cref="OversoldWithRespectedBounce"/>: crosses 75, drops back out
    /// BELOW the touch price after 5 candles, then rallies.
    /// </summary>
    private static List<decimal> OverboughtWithRespectedDrop()
    {
        var closes = new List<decimal> { 100m };
        for (var visit = 0; visit < 2; visit++)
        {
            for (var i = 0; i < 4; i++) closes.Add(closes[^1] + 10m);  // push through the level to RSI >= 75
            for (var i = 0; i < 7; i++) closes.Add(closes[^1] - 15m);  // drop down strongly so price at i+5 is much lower
            for (var i = 0; i < 15; i++) closes.Add(closes[^1] + (i % 2 == 0 ? 0.3m : -0.3m)); // neutral recovery
        }

        for (var i = 0; i < 8; i++) closes.Add(closes[^1] + 15m);      // close back inside overbought >= 75
        return WithWarmup(closes);
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
