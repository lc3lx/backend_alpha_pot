using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// ATR / ADX are the backbone of regime detection, so they are pinned against values
/// produced by an independent implementation (exact decimal arithmetic), the same way
/// RSI and EMA are pinned elsewhere in this suite.
/// </summary>
public sealed class VolatilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Atr_and_adx_match_an_independent_reference()
    {
        var bars = ReferenceBars();

        var atr = Volatility.Atr(bars, 14);
        var adx = Volatility.Adx(bars, 14);

        atr.Should().NotBeNull();
        Math.Round(atr!.Value, 6).Should().Be(2.385804m);

        adx.Should().NotBeNull();
        Math.Round(adx!.Value.Adx, 6).Should().Be(7.908817m);
        Math.Round(adx.Value.PlusDi, 6).Should().Be(32.882414m);
        Math.Round(adx.Value.MinusDi, 6).Should().Be(39.029782m);
    }

    [Fact]
    public void True_range_uses_the_larger_of_the_bar_range_and_the_gaps_to_the_prior_close()
    {
        var bars = new List<RsiCandle>
        {
            Bar(0, close: 100m, high: 101m, low: 99m),
            // Gaps up: high-prevClose (=5) beats the bar's own range (=2).
            Bar(1, close: 104m, high: 105m, low: 103m),
        };

        var tr = Volatility.TrueRangeSeries(bars);

        tr[0].Should().Be(0m);          // no previous bar
        tr[1].Should().Be(5m);          // 105 - 100
    }

    [Fact]
    public void Adx_is_high_in_a_clean_trend_and_low_in_a_range()
    {
        var trending = Volatility.Adx(TrendingUp(80), 14)!.Value;
        var ranging = Volatility.Adx(Ranging(80), 14)!.Value;

        trending.Adx.Should().BeGreaterThan(40m);
        trending.PlusDi.Should().BeGreaterThan(trending.MinusDi);

        ranging.Adx.Should().BeLessThan(25m);
    }

    [Fact]
    public void Adx_direction_flips_for_a_downtrend()
    {
        var down = Volatility.Adx(TrendingDown(80), 14)!.Value;

        down.Adx.Should().BeGreaterThan(40m);
        down.MinusDi.Should().BeGreaterThan(down.PlusDi);
        down.DirectionalBias.Should().BeLessThan(0m);
    }

    [Fact]
    public void Short_history_yields_no_value_rather_than_a_wrong_one()
    {
        var tooShort = TrendingUp(10);

        Volatility.Atr(tooShort, 14).Should().BeNull();
        Volatility.Adx(tooShort, 14).Should().BeNull();
        // ADX needs two smoothing passes — length+1 bars is still not enough.
        Volatility.Adx(TrendingUp(20), 14).Should().BeNull();
    }

    [Fact]
    public void Bars_without_a_real_range_are_reported_as_unusable()
    {
        var closesOnly = Enumerable.Range(0, 40)
            .Select(i => new RsiCandle(Start.AddMinutes(i), 100m + i, Start.AddMinutes(i + 1)))
            .ToList();

        Volatility.HasUsableRange(closesOnly).Should().BeFalse();
        Volatility.HasUsableRange(TrendingUp(40)).Should().BeTrue();

        // It still computes rather than throwing — it just collapses to close-to-close.
        Volatility.Atr(closesOnly, 14).Should().NotBeNull();
    }

    [Fact]
    public void Relative_volume_compares_the_latest_bar_with_its_recent_average()
    {
        // 20 bars of volume 100, then one of 250 → 2.5x normal.
        var bars = Enumerable.Range(0, 21)
            .Select(i => Bar(i, 100m, 101m, 99m, volume: i == 20 ? 250m : 100m))
            .ToList();

        Volatility.RelativeVolume(bars, 20).Should().Be(2.5m);
    }

    [Fact]
    public void Relative_volume_is_null_when_any_bar_lacks_volume()
    {
        // Missing volume is "cannot tell", never "zero activity" — callers must skip
        // the filter rather than treat the market as dead.
        var missingLatest = Enumerable.Range(0, 21)
            .Select(i => Bar(i, 100m, 101m, 99m, volume: i == 20 ? null : 100m))
            .ToList();
        Volatility.RelativeVolume(missingLatest, 20).Should().BeNull();

        var missingHistory = Enumerable.Range(0, 21)
            .Select(i => Bar(i, 100m, 101m, 99m, volume: i == 5 ? null : 100m))
            .ToList();
        Volatility.RelativeVolume(missingHistory, 20).Should().BeNull();

        var noneAtAll = TrendingUp(40);
        Volatility.RelativeVolume(noneAtAll, 20).Should().BeNull();
    }

    [Fact]
    public void Atr_percent_normalises_across_price_levels()
    {
        // Same shape, 100x the price → same ATR%.
        var cheap = Volatility.AtrPercent(TrendingUp(60), 14);
        var rich = Volatility.AtrPercent(Scaled(TrendingUp(60), 100m), 14);

        cheap.Should().NotBeNull();
        rich.Should().NotBeNull();
        Math.Abs(cheap!.Value - rich!.Value).Should().BeLessThan(0.0001m);
    }

    // ---- fixtures ----

    /// <summary>Mirrors the independent reference script exactly.</summary>
    private static List<RsiCandle> ReferenceBars()
    {
        var bars = new List<RsiCandle>();
        var p = 100m;
        for (var i = 0; i < 60; i++)
        {
            p += (i % 7) - 3 + (i % 3 == 0 ? 0.5m : -0.25m);
            var open = p;
            var close = p + ((i % 5) - 2) * 0.4m;
            var high = Math.Max(open, close) + Math.Abs(i % 4) * 0.3m;
            var low = Math.Min(open, close) - Math.Abs(i % 3) * 0.3m;
            bars.Add(new RsiCandle(
                Start.AddMinutes(i), close, Start.AddMinutes(i + 1), high, low, open));
        }

        return bars;
    }

    private static List<RsiCandle> TrendingUp(int count) =>
        Enumerable.Range(0, count)
            .Select(i => Bar(i, 100m + i * 0.5m, 100m + i * 0.5m + 0.2m, 100m + i * 0.5m - 0.1m))
            .ToList();

    private static List<RsiCandle> TrendingDown(int count) =>
        Enumerable.Range(0, count)
            .Select(i => Bar(i, 500m - i * 0.5m, 500m - i * 0.5m + 0.1m, 500m - i * 0.5m - 0.2m))
            .ToList();

    /// <summary>Oscillates in a tight band — no directional movement to accumulate.</summary>
    private static List<RsiCandle> Ranging(int count) =>
        Enumerable.Range(0, count)
            .Select(i =>
            {
                var c = 100m + (i % 2 == 0 ? 0.2m : -0.2m);
                return Bar(i, c, 100.4m, 99.6m);
            })
            .ToList();

    private static List<RsiCandle> Scaled(IEnumerable<RsiCandle> bars, decimal factor) =>
        bars.Select(b => b with
        {
            Close = b.Close * factor,
            High = b.High * factor,
            Low = b.Low * factor,
            Open = b.Open * factor,
        }).ToList();

    private static RsiCandle Bar(int index, decimal close, decimal high, decimal low, decimal? volume = null) =>
        new(Start.AddMinutes(index), close, Start.AddMinutes(index + 1), high, low, close, volume);
}
