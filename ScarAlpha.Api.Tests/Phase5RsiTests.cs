using System.Net;
using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Access;
using ScarAlpha.Infrastructure.Strategies;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class Phase5RsiTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Asset = "EURUSD_otc";

    [Fact]
    public void Correct_calculation_alternating_gains_losses_returns_50()
    {
        var calc = new RsiCalculator();
        var options = RsiStrategyOptions.Default60Seconds with { TimeframeSeconds = 60 };

        // 15 closes => exactly enough for the first RSI value (period = 14).
        var closes = new List<decimal>();
        var price = 100m;
        for (var i = 0; i < 15; i++)
        {
            // Alternate +1 / -1 deltas.
            if (i == 0) closes.Add(price);
            else closes.Add(closes[i - 1] + (i % 2 == 1 ? 1m : -1m));
        }

        // For alternating +1/-1, sum(gains)=sum(losses) => avgGain==avgLoss => RSI == 50.
        calc.CalculateRsi(closes, options).Should().Be(50m);
    }

    [Fact]
    public void All_gains_returns_100_zero_loss_handling()
    {
        var calc = new RsiCalculator();
        var options = RsiStrategyOptions.Default60Seconds;

        var closes = Enumerable.Range(0, 15).Select(i => 100m + i).ToList();
        calc.CalculateRsi(closes, options).Should().Be(100m);
    }

    [Fact]
    public void All_losses_returns_0()
    {
        var calc = new RsiCalculator();
        var options = RsiStrategyOptions.Default60Seconds;

        var closes = Enumerable.Range(0, 15).Select(i => 100m - i).ToList();
        calc.CalculateRsi(closes, options).Should().Be(0m);
    }

    [Fact]
    public void Mixed_gains_losses_computation_is_deterministic()
    {
        var calc = new RsiCalculator();
        var options = RsiStrategyOptions.Default60Seconds;

        // First 15 closes: all gains (+1). Last delta: -1.
        var closes = Enumerable.Range(0, 15).Select(i => 100m + i).ToList();
        closes.Add(closes[^1] - 1m); // length 16

        var rsi1 = calc.CalculateRsi(closes, options);
        var rsi2 = calc.CalculateRsi(closes, options);

        rsi1.Should().Be(rsi2);
        Math.Round(rsi1, 2).Should().Be(92.86m);
    }

    [Fact]
    public async Task Insufficient_candles_throws()
    {
        var calc = new RsiCalculator();
        var svc = new RsiSignalService(calc);
        var options = RsiStrategyOptions.Default60Seconds;

        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var candles = CreateCandles(closes: Enumerable.Range(0, 15).Select(i => 100m - i).ToList(), now: now, timeframeSeconds: 60);

        Func<Task> act = () => svc.GetSignalAsync(UserId, Asset, candles, options, now);
        var ex = await act.Should().ThrowAsync<ApiException>();
        ex.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Oversold_crossing_produces_CALL()
    {
        var calc = new RsiCalculator();
        var svc = new RsiSignalService(calc);
        var options = RsiStrategyOptions.Default60Seconds;

        // Previous RSI = 0 (all losses), current RSI > 30 with a big +10 jump.
        // Closes length = 16 (period + 2).
        var closes = new List<decimal>();
        for (var i = 0; i < 15; i++) closes.Add(100m - i); // 100..86
        closes.Add(96m); // +10 from 86

        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var candles = CreateCandles(closes, now, timeframeSeconds: 60);

        var signal = await svc.GetSignalAsync(UserId, Asset, candles, options, now);
        signal.Signal.Should().Be("Call");
        Math.Round(signal.Rsi, 2).Should().Be(43.48m);
    }

    [Fact]
    public async Task Overbought_crossing_produces_PUT()
    {
        var calc = new RsiCalculator();
        var svc = new RsiSignalService(calc);
        var options = RsiStrategyOptions.Default60Seconds;

        // Previous RSI = 100 (all gains), current RSI < 70 with a big -10 drop.
        // Closes length = 16.
        var closes = Enumerable.Range(0, 15).Select(i => 100m + i).ToList(); // 100..114
        closes.Add(104m); // -10 from 114

        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var candles = CreateCandles(closes, now, timeframeSeconds: 60);

        var signal = await svc.GetSignalAsync(UserId, Asset, candles, options, now);
        signal.Signal.Should().Be("Put");
        Math.Round(signal.Rsi, 2).Should().Be(56.52m);
    }

    [Fact]
    public async Task No_crossing_returns_NONE()
    {
        var calc = new RsiCalculator();
        var svc = new RsiSignalService(calc);
        var options = RsiStrategyOptions.Default60Seconds;

        var closes = Enumerable.Repeat(100m, 16).ToList(); // flat => RSI 50, no crossing
        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var candles = CreateCandles(closes, now, timeframeSeconds: 60);

        var signal = await svc.GetSignalAsync(UserId, Asset, candles, options, now);
        signal.Signal.Should().Be("None");
        signal.Rsi.Should().Be(50m);
    }

    [Fact]
    public async Task Does_not_repeat_signal_for_same_candle()
    {
        var calc = new RsiCalculator();
        var svc = new RsiSignalService(calc);
        var options = RsiStrategyOptions.Default60Seconds;

        var closes = new List<decimal>();
        for (var i = 0; i < 15; i++) closes.Add(100m - i); // 100..86
        closes.Add(96m); // +10 from 86

        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var candles = CreateCandles(closes, now, timeframeSeconds: 60);

        var first = await svc.GetSignalAsync(UserId, Asset, candles, options, now);
        first.Signal.Should().Be("Call");

        // Same candleTime => should return None on repeat.
        var second = await svc.GetSignalAsync(UserId, Asset, candles, options, now);
        second.Signal.Should().Be("None");
        second.CandleTime.Should().Be(first.CandleTime);
    }

    [Fact]
    public async Task Closed_candles_only_ignores_open_last_candle()
    {
        var calc = new RsiCalculator();
        var svc = new RsiSignalService(calc);
        var options = RsiStrategyOptions.Default60Seconds;

        // Provide 17 candles total. Last one is OPEN (endTimestamp in the future).
        // After filtering closed candles, we should compute signal based on candle #15 only (no crossing).
        var closedCloses = Enumerable.Range(0, 16).Select(i => 100m - i).ToList(); // length 16 => all losses
        var openCandleClose = 110m; // would create oversold crossing if used as current, but must be ignored.
        closedCloses.Add(openCandleClose); // length 17 total

        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var timeframeSeconds = 60;
        var candles = CreateCandles(closedCloses, now, timeframeSeconds, openLastIndex: 16);

        var signal = await svc.GetSignalAsync(UserId, Asset, candles, options, now);
        signal.Signal.Should().Be("None");

        // CandleTime must correspond to the last CLOSED candle (index 15, not index 16).
        var expectedClosedCandleTime = candles.Where(c => c.EndTimestamp is null || c.EndTimestamp <= now).OrderBy(c => c.Timestamp).Last().Timestamp;
        signal.CandleTime.Should().Be(expectedClosedCandleTime);
    }

    [Fact]
    public async Task Deterministic_when_signal_is_NONE()
    {
        var calc = new RsiCalculator();
        var svc = new RsiSignalService(calc);
        var options = RsiStrategyOptions.Default60Seconds;

        var closes = Enumerable.Repeat(100m, 16).ToList();
        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var candles = CreateCandles(closes, now, timeframeSeconds: 60);

        var signal1 = await svc.GetSignalAsync(UserId, Asset, candles, options, now);
        var signal2 = await svc.GetSignalAsync(UserId, Asset, candles, options, now);

        signal1.Signal.Should().Be("None");
        signal2.Signal.Should().Be("None");
        signal2.Rsi.Should().Be(signal1.Rsi);
    }

    private static List<RsiCandle> CreateCandles(
        List<decimal> closes,
        DateTimeOffset now,
        int timeframeSeconds,
        int? openLastIndex = null)
    {
        var start = now - TimeSpan.FromSeconds((closes.Count - 1) * timeframeSeconds);

        return closes.Select((close, idx) =>
        {
            var timestamp = start + TimeSpan.FromSeconds(idx * timeframeSeconds);
            var endTimestamp = openLastIndex is not null && idx == openLastIndex
                ? now + TimeSpan.FromSeconds(10)
                : now - TimeSpan.FromSeconds(1);

            return new RsiCandle(timestamp, close, endTimestamp);
        }).ToList();
    }
}

