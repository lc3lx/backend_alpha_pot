using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The regime decides which strategy is allowed to trade, so its rules are pinned
/// exhaustively over scalars and then spot-checked on realistic series.
/// </summary>
public sealed class MarketRegimeTests
{
    /// <summary>Start from plain defaults, whatever ran before this class.</summary>
    public MarketRegimeTests() => StrategyDefaults.Reset();

    private static readonly DateTimeOffset Start = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
    private static MarketRegimeOptions Options => MarketRegimeOptions.Default;

    // ---- classification over scalars ----

    [Theory]
    // Healthy trends.
    [InlineData(30, 40, 15, MarketRegime.Uptrend)]
    [InlineData(30, 15, 40, MarketRegime.Downtrend)]
    [InlineData(25, 30, 20, MarketRegime.Uptrend)]      // exactly at TrendAdxMin
    // Trend strength without a clear direction.
    [InlineData(30, 26, 25, MarketRegime.Unclear)]      // DI gap below DiSeparationMin
    // Ranges.
    [InlineData(15, 25, 24, MarketRegime.Sideways)]
    [InlineData(20, 10, 12, MarketRegime.Sideways)]     // exactly at RangeAdxMax
    // The deliberate no-man's-land between the two thresholds.
    [InlineData(22, 40, 10, MarketRegime.Unclear)]
    public void Classify_maps_adx_and_di_to_a_regime(
        double adx, double plusDi, double minusDi, MarketRegime expected)
    {
        var inputs = Healthy() with
        {
            Adx = (decimal)adx,
            PlusDi = (decimal)plusDi,
            MinusDi = (decimal)minusDi,
        };

        MarketRegimeClassifier.Classify(inputs, Options, out _).Should().Be(expected);
    }

    [Fact]
    public void A_dead_market_is_unclear_not_sideways()
    {
        // A flat tape routed to RSI mean-reversion is where binary options bleed
        // against the payout, so it must not be tradable at all.
        var thinRelativeToItself = Healthy() with { Adx = 10m, AtrRatio = 0.3m };
        MarketRegimeClassifier
            .Classify(thinRelativeToItself, Options, out var reason)
            .Should().Be(MarketRegime.Unclear);
        reason.Should().Be(MarketRegimeClassifier.ReasonDeadMarket);

        // Catches a pair that has been dead for the whole lookback, where the ratio
        // still reads ~1.0 but the absolute movement is negligible.
        var absolutelyFlat = Healthy() with { Adx = 10m, AtrRatio = 1.0m, AtrBps = 0.1m };
        MarketRegimeClassifier
            .Classify(absolutelyFlat, Options, out _)
            .Should().Be(MarketRegime.Unclear);
    }

    [Fact]
    public void Missing_bars_are_unclear()
    {
        var inputs = Healthy() with { HasEnoughBars = false };

        MarketRegimeClassifier.Classify(inputs, Options, out var reason)
            .Should().Be(MarketRegime.Unclear);
        reason.Should().Be(MarketRegimeClassifier.ReasonInsufficientData);
    }

    [Fact]
    public void Volume_never_changes_the_regime_itself()
    {
        // Volume can only veto an entry later; it must not turn a trend into a range.
        var thin = Healthy() with { Adx = 30m, PlusDi = 40m, MinusDi = 15m, RelativeVolume = 0.1m };

        MarketRegimeClassifier.Classify(thin, Options, out _).Should().Be(MarketRegime.Uptrend);
    }

    // ---- analysis over real series ----

    [Fact]
    public void A_steady_rise_is_an_uptrend_and_a_steady_fall_is_a_downtrend()
    {
        MarketRegimeClassifier.Analyze(TrendingUp(220), Options).Regime
            .Should().Be(MarketRegime.Uptrend);

        MarketRegimeClassifier.Analyze(TrendingDown(220), Options).Regime
            .Should().Be(MarketRegime.Downtrend);
    }

    [Fact]
    public void A_choppy_band_is_sideways()
    {
        var snapshot = MarketRegimeClassifier.Analyze(Ranging(220), Options);

        snapshot.Regime.Should().Be(MarketRegime.Sideways);
        snapshot.Adx.Should().BeLessThan(Options.RangeAdxMax);
    }

    [Fact]
    public void Too_little_history_reports_insufficient_data()
    {
        var snapshot = MarketRegimeClassifier.Analyze(TrendingUp(40), Options);

        snapshot.Regime.Should().Be(MarketRegime.Unclear);
        snapshot.Reason.Should().Be(MarketRegimeClassifier.ReasonInsufficientData);
    }

    [Fact]
    public void Regime_needs_no_more_history_than_rsi_already_requires()
    {
        // Worth pinning: it means regime detection adds zero extra broker traffic.
        IndicatorWarmup.ForAdx(14).Should().BeLessThanOrEqualTo(IndicatorWarmup.ForRsi(14));
        IndicatorWarmup.ForAtr(14).Should().BeLessThanOrEqualTo(IndicatorWarmup.ForRsi(14));
    }

    // ---- the volume filter ----

    [Fact]
    public void Absent_volume_disables_the_filter_rather_than_blocking()
    {
        // Binolla leaves volume null on every forming bar and may never send it.
        // "Cannot tell" must not read as "no activity".
        var snapshot = MarketRegimeClassifier.Analyze(TrendingUp(220), Options);

        snapshot.VolumeAvailable.Should().BeFalse();
        snapshot.VolumeOk.Should().BeTrue();
        snapshot.RelativeVolume.Should().BeNull();
        snapshot.Regime.Should().Be(MarketRegime.Uptrend);
    }

    [Fact]
    public void Present_but_thin_volume_fails_the_filter()
    {
        var healthy = MarketRegimeClassifier.Analyze(TrendingUp(220, volume: 100m), Options);
        healthy.VolumeAvailable.Should().BeTrue();
        healthy.VolumeOk.Should().BeTrue();
        healthy.RelativeVolume.Should().Be(1m);

        var thin = TrendingUp(220, volume: 100m);
        thin[^1] = thin[^1] with { Volume = 10m };   // a tenth of normal
        var snapshot = MarketRegimeClassifier.Analyze(thin, Options);

        snapshot.VolumeAvailable.Should().BeTrue();
        snapshot.VolumeOk.Should().BeFalse();
        // The regime itself is untouched — only the veto flag changes.
        snapshot.Regime.Should().Be(MarketRegime.Uptrend);
    }

    [Fact]
    public void Turning_the_volume_filter_off_stops_it_vetoing()
    {
        var thin = TrendingUp(220, volume: 100m);
        thin[^1] = thin[^1] with { Volume = 10m };

        var snapshot = MarketRegimeClassifier.Analyze(
            thin, Options with { VolumeFilterEnabled = false });

        snapshot.VolumeOk.Should().BeTrue();
    }

    // ---- fixtures ----

    private static RegimeInputs Healthy() => new(
        Adx: 30m, PlusDi: 40m, MinusDi: 15m,
        AtrRatio: 1m, AtrBps: 5m,
        RelativeVolume: null, HasEnoughBars: true);

    private static List<RsiCandle> TrendingUp(int count, decimal? volume = null) =>
        Enumerable.Range(0, count)
            .Select(i =>
            {
                var c = 100m + i * 0.5m;
                return Bar(i, c, c + 0.2m, c - 0.1m, volume);
            })
            .ToList();

    private static List<RsiCandle> TrendingDown(int count, decimal? volume = null) =>
        Enumerable.Range(0, count)
            .Select(i =>
            {
                var c = 500m - i * 0.5m;
                return Bar(i, c, c + 0.1m, c - 0.2m, volume);
            })
            .ToList();

    /// <summary>Oscillates inside a band — plenty of range, no net direction.</summary>
    private static List<RsiCandle> Ranging(int count) =>
        Enumerable.Range(0, count)
            .Select(i =>
            {
                var c = 100m + (i % 4 switch { 0 => 0.3m, 1 => -0.2m, 2 => 0.1m, _ => -0.3m });
                return Bar(i, c, 100.6m, 99.4m);
            })
            .ToList();

    private static RsiCandle Bar(int index, decimal close, decimal high, decimal low, decimal? volume = null) =>
        new(Start.AddMinutes(index), close, Start.AddMinutes(index + 1), high, low, close, volume);
}
