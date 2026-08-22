using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Infrastructure.Strategies;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Covers the Pine port: EMA 9/21 cross, the RSI gate, the HTF trend filter, and the
/// closed-bar entry discipline. Fixture shapes were cross-checked against an
/// independent implementation of ta.ema / ta.rsi.
/// </summary>
public sealed class EmaRsiStrategyTests
{
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Asset = "EURUSD_otc";
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);

    private static EmaRsiOptions NoTrend => EmaRsiOptions.Default with { UseTrendFilter = false };

    [Fact]
    public void Ema_is_sma_seeded_like_pine_ta_ema()
    {
        var values = Enumerable.Range(1, 10).Select(i => (decimal)i).ToList();

        var ema = Indicators.EmaSeries(values, 5);

        // Seed bar = SMA of the first 5 values = 3.
        ema[4].Should().Be(3m);
        // Then alpha = 2/(5+1) = 1/3: 3 + (6-3)/3 = 4.
        // (1/3 has no exact decimal form, so compare within a rounding step.)
        ema[5].Should().BeApproximately(4m, 0.0000000001m);
        // Values before the seed are undefined and left at zero.
        ema[3].Should().Be(0m);
    }

    [Fact]
    public async Task Crossover_with_rsi_in_range_emits_call()
    {
        var service = new EmaRsiSignalService();
        var candles = ClosedCandles(CallCross());

        var signal = await service.GetSignalAsync(
            UserId, Asset, candles, TrendUp(), EmaRsiOptions.Default, Now);

        signal.Signal.Should().Be("Call");
        signal.StrategyId.Should().Be("ema");
        signal.CandleTime.Should().Be(Now);
        signal.AutomationError.Should().BeNull();
        signal.Rsi.Should().BeLessThanOrEqualTo(EmaRsiOptions.Default.RsiBuyMax);
    }

    [Fact]
    public async Task Crossunder_with_rsi_in_range_emits_put()
    {
        var service = new EmaRsiSignalService();
        var candles = ClosedCandles(PutCross());

        var signal = await service.GetSignalAsync(
            UserId, Asset, candles, TrendDown(), EmaRsiOptions.Default, Now);

        signal.Signal.Should().Be("Put");
        signal.Rsi.Should().BeGreaterThanOrEqualTo(EmaRsiOptions.Default.RsiSellMin);
    }

    [Fact]
    public async Task Crossover_with_rsi_above_buy_max_is_skipped()
    {
        var service = new EmaRsiSignalService();
        // One sharp up bar off a flat base crosses instantly at RSI 100.
        var closes = Flat(200).Concat(new[] { 101m }).ToList();

        var signal = await service.GetSignalAsync(
            UserId, Asset, ClosedCandles(closes), TrendUp(), EmaRsiOptions.Default, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().Be(100m);
    }

    [Fact]
    public async Task Crossunder_with_rsi_below_sell_min_is_skipped()
    {
        var service = new EmaRsiSignalService();
        var closes = Flat(200).Concat(new[] { 99m }).ToList();

        var signal = await service.GetSignalAsync(
            UserId, Asset, ClosedCandles(closes), TrendDown(), EmaRsiOptions.Default, Now);

        signal.Signal.Should().Be("None");
        signal.Rsi.Should().Be(0m);
    }

    [Fact]
    public async Task No_cross_emits_none()
    {
        var service = new EmaRsiSignalService();
        // The bar BEFORE the cross: EMAs have not crossed yet.
        var closes = CallCross();
        closes.RemoveAt(closes.Count - 1);

        var signal = await service.GetSignalAsync(
            UserId, Asset, ClosedCandles(closes), TrendUp(), EmaRsiOptions.Default, Now);

        signal.Signal.Should().Be("None");
    }

    [Fact]
    public async Task Trend_filter_blocks_a_cross_that_fights_the_higher_timeframe()
    {
        var service = new EmaRsiSignalService();
        var candles = ClosedCandles(CallCross());

        var against = await service.GetSignalAsync(
            UserId, Asset, candles, TrendDown(), EmaRsiOptions.Default, Now);
        against.Signal.Should().Be("None");

        var withTrend = await service.GetSignalAsync(
            UserId, Asset, candles, TrendUp(), EmaRsiOptions.Default, Now);
        withTrend.Signal.Should().Be("Call");
    }

    [Fact]
    public async Task Unconfirmed_trend_never_counts_as_agreement()
    {
        var service = new EmaRsiSignalService();
        var candles = ClosedCandles(CallCross());

        // Too little HTF history to seed EMA200 → Unknown, and Unknown must not trade.
        var blocked = await service.GetSignalAsync(
            UserId, Asset, candles, Array.Empty<decimal>(), EmaRsiOptions.Default, Now);
        blocked.Signal.Should().Be("None");

        // Same bars trade once the filter is explicitly turned off.
        var allowed = await service.GetSignalAsync(
            UserId, Asset, candles, Array.Empty<decimal>(), NoTrend, Now);
        allowed.Signal.Should().Be("Call");
    }

    [Fact]
    public void Resolve_trend_reports_unknown_below_ema200_warmup()
    {
        var options = EmaRsiOptions.Default;

        EmaRsiEngine.ResolveTrend(Rising(150), options).Should().Be(EmaRsiTrend.Unknown);
        EmaRsiEngine.ResolveTrend(Rising(260), options).Should().Be(EmaRsiTrend.Up);
        EmaRsiEngine.ResolveTrend(Falling(260), options).Should().Be(EmaRsiTrend.Down);
    }

    [Fact]
    public async Task Forming_bar_cannot_trigger_a_cross()
    {
        var service = new EmaRsiSignalService();
        var closes = CallCross();
        var start = Now - TimeSpan.FromMinutes(closes.Count - 1);
        // Last bar starts at Now, so at Now+30s it is still forming.
        var candles = closes
            .Select((c, i) => new RsiCandle(start.AddMinutes(i), c, null))
            .ToList();

        var signal = await service.GetSignalAsync(
            UserId, Asset, candles, TrendUp(), NoTrend with { UseTrendFilter = true }, Now.AddSeconds(30));

        // The cross bar has not closed, so the closed series ends one bar earlier — no cross.
        signal.Signal.Should().Be("None");
    }

    [Fact]
    public async Task Setup_expires_after_the_entry_window()
    {
        var service = new EmaRsiSignalService();
        var candles = ClosedCandles(CallCross());

        (await service.GetSignalAsync(UserId, Asset, candles, TrendUp(), EmaRsiOptions.Default, Now))
            .Signal.Should().Be("Call");
        (await service.GetSignalAsync(UserId, Asset, candles, TrendUp(), EmaRsiOptions.Default, Now.AddSeconds(4)))
            .Signal.Should().Be("Call");

        var expired = await service.GetSignalAsync(
            UserId, Asset, candles, TrendUp(), EmaRsiOptions.Default, Now.AddSeconds(6));
        expired.Signal.Should().Be("None");
        expired.AutomationError.Should().Be("SETUP_EXPIRED");
    }

    [Fact]
    public async Task One_cross_cannot_open_two_trades()
    {
        var service = new EmaRsiSignalService();
        var candles = ClosedCandles(CallCross());

        var first = await service.GetSignalAsync(UserId, Asset, candles, TrendUp(), EmaRsiOptions.Default, Now);
        first.Signal.Should().Be("Call");

        service.MarkSignalEmitted(UserId, Asset, 60, first.CandleTime);

        var second = await service.GetSignalAsync(UserId, Asset, candles, TrendUp(), EmaRsiOptions.Default, Now);
        second.Signal.Should().Be("None");
        second.AutomationError.Should().Be("SETUP_CONSUMED");
    }

    [Fact]
    public async Task Short_history_is_rejected_rather_than_guessed()
    {
        var service = new EmaRsiSignalService();
        var candles = ClosedCandles(Flat(40).ToList());

        var act = () => service.GetSignalAsync(
            UserId, Asset, candles, TrendUp(), EmaRsiOptions.Default, Now);

        var error = await act.Should().ThrowAsync<ApiException>();
        error.Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    [Fact]
    public void Bot_duration_maps_to_pine_trade_duration_bars()
    {
        EmaRsiOptions.FromBotDurationSeconds(180).ExpiryCandles.Should().Be(3);
        EmaRsiOptions.FromBotDurationSeconds(240).ExpiryCandles.Should().Be(4);
        EmaRsiOptions.FromBotDurationSeconds(300).ExpiryCandles.Should().Be(5);
        EmaRsiOptions.FromBotDurationSeconds(60).ExpiryCandles.Should().Be(5);
    }

    [Fact]
    public void Aggregate_closes_folds_1m_bars_into_complete_15m_buckets()
    {
        // 30 one-minute bars aligned to 10:00 → exactly two complete 15m buckets.
        var start = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 30)
            .Select(i => new RsiCandle(start.AddMinutes(i), 100m + i, null))
            .ToList();

        var htf = EmaRsiEngine.AggregateCloses(candles, 900);

        htf.Should().HaveCount(2);
        htf[0].Should().Be(114m);   // close of 10:00–10:14
        htf[1].Should().Be(129m);   // close of 10:15–10:29

        // A partial trailing bucket is dropped, never emitted as a finished bar.
        EmaRsiEngine.AggregateCloses(candles.Take(20).ToList(), 900).Should().HaveCount(1);
    }

    // ---- fixtures (values verified against an independent ta.ema/ta.rsi implementation) ----

    private static IEnumerable<decimal> Flat(int n) => Enumerable.Repeat(100m, n);

    /// <summary>Flat base, decline, then a slow recovery that crosses EMA9 up through EMA21 at RSI ~59.</summary>
    private static List<decimal> CallCross()
    {
        var p = Flat(200).ToList();
        for (var i = 0; i < 10; i++) p.Add(p[^1] - 1.5m);
        for (var i = 0; i < 16; i++) p.Add(p[^1] + 0.5m);
        return p;
    }

    /// <summary>Mirror of <see cref="CallCross"/>: crosses EMA9 down through EMA21 at RSI ~41.</summary>
    private static List<decimal> PutCross()
    {
        var p = Flat(200).ToList();
        for (var i = 0; i < 10; i++) p.Add(p[^1] + 1.5m);
        for (var i = 0; i < 16; i++) p.Add(p[^1] - 0.5m);
        return p;
    }

    private static List<decimal> Rising(int n) =>
        Enumerable.Range(0, n).Select(i => 100m + i * 0.5m).ToList();

    private static List<decimal> Falling(int n) =>
        Enumerable.Range(0, n).Select(i => 500m - i * 0.5m).ToList();

    private static IReadOnlyList<decimal> TrendUp() => Rising(260);
    private static IReadOnlyList<decimal> TrendDown() => Falling(260);

    /// <summary>All bars closed; the last one closes exactly at <see cref="Now"/>.</summary>
    private static List<RsiCandle> ClosedCandles(List<decimal> closes)
    {
        var start = Now - TimeSpan.FromMinutes(closes.Count);
        return closes
            .Select((c, i) => new RsiCandle(start.AddMinutes(i), c, Now.AddSeconds(-1)))
            .ToList();
    }
}
