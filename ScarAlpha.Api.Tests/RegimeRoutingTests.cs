using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Regime routing governs the SMART strategy only: it fades ranges with RSI, rides trends
/// with EMA, and sits out an unclear or dead market.
///
/// Every other strategy is deliberately untouched — RSI runs on its own levels and
/// backtest, EMA on its own cross, the pattern rule on its own pattern. These tests pin
/// that isolation, because a filter silently leaking across strategies is exactly the
/// kind of bug that makes "why didn't it enter?" unanswerable.
/// </summary>
public sealed class RegimeRoutingTests
{
    /// <summary>Start from plain defaults, whatever ran before this class.</summary>
    public RegimeRoutingTests() => StrategyDefaults.Reset();

    [Theory]
    // Every non-smart strategy is left completely alone by the regime, in any market.
    [InlineData(MarketRegime.Uptrend, "rsi", "Put")]
    [InlineData(MarketRegime.Downtrend, "rsi", "Call")]
    [InlineData(MarketRegime.Sideways, "ema", "Call")]
    [InlineData(MarketRegime.Uptrend, "ema", "Put")]
    [InlineData(MarketRegime.Unclear, "rsi", "Call")]
    [InlineData(MarketRegime.Unclear, "ema", "Put")]
    public void A_pinned_strategy_is_never_filtered_by_the_regime(
        MarketRegime regime, string engine, string side)
    {
        // RSI must depend on its own levels and backtest only; EMA on its own cross only.
        // Mixing the regime in would mean a strategy no longer does what its name says.
        var ok = RegimeRouting.IsAllowed(
            botStrategyId: engine, engineId: engine, side: side,
            regime: Snapshot(regime), out var code);

        ok.Should().BeTrue();
        code.Should().BeNull();
    }

    [Fact]
    public void The_pattern_strategy_is_also_left_alone()
    {
        RegimeRouting.IsAllowed("alt5", "alt5", "Call", Snapshot(MarketRegime.Unclear), out _)
            .Should().BeTrue();
    }

    [Theory]
    // Smart mode is the only one the regime governs.
    [InlineData(MarketRegime.Sideways, "rsi", "Call", true)]
    [InlineData(MarketRegime.Sideways, "rsi", "Put", true)]
    [InlineData(MarketRegime.Uptrend, "ema", "Call", true)]
    [InlineData(MarketRegime.Downtrend, "ema", "Put", true)]
    [InlineData(MarketRegime.Uptrend, "ema", "Put", false)]     // counter-trend
    [InlineData(MarketRegime.Downtrend, "ema", "Call", false)]  // counter-trend
    [InlineData(MarketRegime.Sideways, "ema", "Call", false)]   // wrong engine for a range
    [InlineData(MarketRegime.Uptrend, "rsi", "Call", false)]    // wrong engine for a trend
    [InlineData(MarketRegime.Unclear, "rsi", "Call", false)]
    [InlineData(MarketRegime.Unclear, "ema", "Put", false)]
    public void Smart_mode_allows_only_the_engine_and_side_the_regime_calls_for(
        MarketRegime regime, string engine, string side, bool allowed)
    {
        var ok = RegimeRouting.IsAllowed(
            botStrategyId: "smart", engineId: engine, side: side,
            regime: Snapshot(regime), out var code);

        ok.Should().Be(allowed);
        if (!allowed) code.Should().NotBeNull();
    }

    [Theory]
    [InlineData(MarketRegime.Sideways, "rsi")]
    [InlineData(MarketRegime.Uptrend, "ema")]
    [InlineData(MarketRegime.Downtrend, "ema")]
    public void Smart_mode_picks_the_engine_that_suits_the_regime(
        MarketRegime regime, string expectedEngine)
    {
        RegimeRouting.EngineFor(regime).Should().Be(expectedEngine);
    }

    [Fact]
    public void Smart_mode_sits_out_an_unclear_market()
    {
        RegimeRouting.EngineFor(MarketRegime.Unclear).Should().BeNull();
    }

    [Fact]
    public void Smart_mode_rejects_a_signal_from_the_wrong_engine_for_the_regime()
    {
        // The regime moved between producing and validating — stale routing must not trade.
        var ok = RegimeRouting.IsAllowed(
            botStrategyId: "smart", engineId: "rsi", side: "Call",
            regime: Snapshot(MarketRegime.Uptrend), out var code);

        ok.Should().BeFalse();
        code.Should().Be(RegimeRouting.RejectWrongRegime);
    }

    [Fact]
    public void A_dead_market_is_reported_distinctly_from_a_merely_unclear_one()
    {
        var dead = Snapshot(MarketRegime.Unclear) with
        {
            Reason = MarketRegimeClassifier.ReasonDeadMarket,
        };

        RegimeRouting.IsAllowed("smart", "rsi", "Call", dead, out var deadCode).Should().BeFalse();
        deadCode.Should().Be(RegimeRouting.RejectDeadMarket);

        var band = Snapshot(MarketRegime.Unclear) with
        {
            Reason = MarketRegimeClassifier.ReasonAdxBand,
        };
        RegimeRouting.IsAllowed("smart", "rsi", "Call", band, out var bandCode).Should().BeFalse();
        bandCode.Should().Be(RegimeRouting.RejectUnclear);
    }

    [Fact]
    public void Thin_volume_vetoes_but_only_when_volume_actually_exists()
    {
        var thin = Snapshot(MarketRegime.Sideways) with
        {
            VolumeAvailable = true,
            VolumeOk = false,
            RelativeVolume = 0.2m,
        };
        RegimeRouting.IsAllowed("smart", "rsi", "Call", thin, out var code).Should().BeFalse();
        code.Should().Be(RegimeRouting.RejectLowVolume);

        // Binolla usually sends no volume at all — that must not block anything.
        var noVolume = Snapshot(MarketRegime.Sideways) with
        {
            VolumeAvailable = false,
            VolumeOk = true,
            RelativeVolume = null,
        };
        RegimeRouting.IsAllowed("smart", "rsi", "Call", noVolume, out _).Should().BeTrue();
    }

    [Fact]
    public void No_regime_measured_leaves_behaviour_untouched()
    {
        RegimeRouting.IsAllowed("smart", "rsi", "Put", regime: null, out var code).Should().BeTrue();
        code.Should().BeNull();
    }

    [Fact]
    public void Gate_does_not_apply_the_regime_while_the_feature_is_off()
    {
        var previous = StrategyGate.RegimeEnabled;
        try
        {
            var signal = EmaSignal("Put");
            var against = Snapshot(MarketRegime.Uptrend);   // counter-trend

            StrategyGate.RegimeEnabled = false;
            StrategyGate.TryValidateForTrade(signal, signal.CandleTime, "smart", against, out _)
                .Should().BeTrue();

            StrategyGate.RegimeEnabled = true;
            StrategyGate.TryValidateForTrade(signal, signal.CandleTime, "smart", against, out var code)
                .Should().BeFalse();
            code.Should().Be(RegimeRouting.RejectCounterTrend);

            // A bot pinned to EMA is untouched even with the regime switch on.
            StrategyGate.TryValidateForTrade(signal, signal.CandleTime, "ema", against, out _)
                .Should().BeTrue();
        }
        finally
        {
            StrategyGate.RegimeEnabled = previous;
        }
    }

    private static RegimeSnapshot Snapshot(MarketRegime regime) =>
        new(regime, Adx: 30m, PlusDi: 40m, MinusDi: 15m,
            Atr: 1m, AtrRatio: 1m, AtrBps: 5m,
            RelativeVolume: null, VolumeAvailable: false, VolumeOk: true, Reason: null);

    private static StrategySignal EmaSignal(string side)
    {
        var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        return new StrategySignal(
            StrategyId: "ema",
            Asset: "EURUSD_otc",
            Signal: side,
            Rsi: 50m,
            CandleTime: now,
            Timeframe: "60",
            Backtest: new RsiBacktestStats(0, 0, 0, 0m, 0, 5, 0m, true));
    }
}
