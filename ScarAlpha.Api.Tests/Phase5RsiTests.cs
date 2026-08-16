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
    public async Task Closed_oversold_rsi_emits_call_without_waiting_for_a_crossing()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var candles = CreateCandles(Enumerable.Range(0, 26).Select(i => 100m - i).ToList());

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("Call");
        signal.Rsi.Should().Be(0m);
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.TotalSignals.Should().BeGreaterThan(0);
        signal.Backtest.SuccessRate.Should().Be(0m);
    }

    [Fact]
    public async Task Closed_overbought_rsi_emits_put_without_waiting_for_a_crossing()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var candles = CreateCandles(Enumerable.Range(0, 26).Select(i => 100m + i).ToList());

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("Put");
        signal.Rsi.Should().Be(100m);
        signal.Backtest!.SuccessRate.Should().Be(0m);
    }

    [Fact]
    public async Task Failed_historical_filter_suppresses_the_current_signal()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var candles = CreateCandles(Enumerable.Range(0, 26).Select(i => 100m - i).ToList());

        var signal = await service.GetSignalAsync(UserId, Asset, candles, RsiStrategyOptions.Default60Seconds, Now);

        signal.Signal.Should().Be("None");
        signal.Backtest.Should().NotBeNull();
        signal.Backtest!.Passed.Should().BeFalse();
        signal.Backtest.MinimumSuccessRate.Should().Be(75m);
    }

    [Fact]
    public async Task Open_candle_is_never_used_for_signal_or_backtest()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var closes = Enumerable.Range(0, 22).Select(i => 100m - i).Append(110m).ToList();
        var candles = CreateCandles(closes, openLastIndex: 22);

        var signal = await service.GetSignalAsync(UserId, Asset, candles, options, Now);

        signal.Signal.Should().Be("Call");
        signal.CandleTime.Should().Be(candles[21].Timestamp);
    }

    [Fact]
    public async Task A_signal_is_not_repeated_for_the_same_closed_candle()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { MinimumSuccessRate = 0m };
        var candles = CreateCandles(Enumerable.Range(0, 26).Select(i => 100m - i).ToList());

        (await service.GetSignalAsync(UserId, Asset, candles, options, Now)).Signal.Should().Be("Call");
        (await service.GetSignalAsync(UserId, Asset, candles, options, Now)).Signal.Should().Be("None");
    }

    [Fact]
    public async Task One_minute_is_the_only_supported_timeframe()
    {
        var service = new RsiSignalService(new RsiCalculator());
        var options = RsiStrategyOptions.Default60Seconds with { TimeframeSeconds = 300 };
        var candles = CreateCandles(Enumerable.Range(0, 26).Select(i => 100m - i).ToList());

        var act = () => service.GetSignalAsync(UserId, Asset, candles, options, Now);
        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
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
