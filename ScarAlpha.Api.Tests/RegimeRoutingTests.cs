using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The rule that makes the bot selective: RSI only fades ranges, EMA only rides trends,
/// and nothing trades against the trend or in a dead market.
/// </summary>
public sealed class RegimeRoutingTests
{
    [Theory]
    // A range is RSI's home — both directions are legal there.
    [InlineData(MarketRegime.Sideways, "rsi", "Call", true)]
    [InlineData(MarketRegime.Sideways, "rsi", "Put", true)]
    // Fading a real trend is how mean-reversion bleeds.
    [InlineData(MarketRegime.Uptrend, "rsi", "Put", false)]
    [InlineData(MarketRegime.Downtrend, "rsi", "Call", false)]
    // EMA needs a trend behind the cross.
    [InlineData(MarketRegime.Uptrend, "ema", "Call", true)]
    [InlineData(MarketRegime.Downtrend, "ema", "Put", true)]
    [InlineData(MarketRegime.Sideways, "ema", "Call", false)]
    // Never take the wrong side of a trend, whichever engine produced it.
    [InlineData(MarketRegime.Uptrend, "ema", "Put", false)]
    [InlineData(MarketRegime.Downtrend, "ema", "Call", false)]
    // No edge either way.
    [InlineData(MarketRegime.Unclear, "rsi", "Call", false)]
    [InlineData(MarketRegime.Unclear, "ema", "Put", false)]
    public void Engine_is_only_allowed_in_the_regime_it_works_in(
        MarketRegime regime, string engine, string side, bool allowed)
    {
        var ok = RegimeRouting.IsAllowed(
            botStrategyId: engine, engineId: engine, side: side,
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
        RegimeRouting.IsAllowed("rsi", "rsi", "Call", thin, out var code).Should().BeFalse();
        code.Should().Be(RegimeRouting.RejectLowVolume);

        // Binolla usually sends no volume at all — that must not block anything.
        var noVolume = Snapshot(MarketRegime.Sideways) with
        {
            VolumeAvailable = false,
            VolumeOk = true,
            RelativeVolume = null,
        };
        RegimeRouting.IsAllowed("rsi", "rsi", "Call", noVolume, out _).Should().BeTrue();
    }

    [Fact]
    public void No_regime_measured_leaves_behaviour_untouched()
    {
        RegimeRouting.IsAllowed("rsi", "rsi", "Put", regime: null, out var code).Should().BeTrue();
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
            StrategyGate.TryValidateForTrade(signal, signal.CandleTime, "ema", against, out _)
                .Should().BeTrue();

            StrategyGate.RegimeEnabled = true;
            StrategyGate.TryValidateForTrade(signal, signal.CandleTime, "ema", against, out var code)
                .Should().BeFalse();
            code.Should().Be(RegimeRouting.RejectCounterTrend);
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
