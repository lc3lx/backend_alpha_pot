namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Which engine may run, and in which direction, given the market regime.
///
/// <para>The rule behind all of it: RSI mean-reversion only pays in a range, and an EMA
/// cross only pays with the trend behind it. Running either in the wrong regime is the
/// main way this bot loses.</para>
///
/// <para>This applies to <see cref="SmartStrategyId"/> ONLY. A bot set to <c>rsi</c>,
/// <c>ema</c> or the pattern rule is left completely alone — each runs its own rules and
/// nothing else, so the strategies never bleed into one another.</para>
/// </summary>
public static class RegimeRouting
{
    /// <summary>Strategy id that lets the regime pick the engine.</summary>
    public const string SmartStrategyId = "smart";
    public const string RsiStrategyId = "rsi";
    public const string EmaStrategyId = "ema";

    public const string RejectUnclear = "REGIME_UNCLEAR";
    public const string RejectDeadMarket = "REGIME_DEAD_MARKET";
    public const string RejectCounterTrend = "REGIME_COUNTER_TREND";
    public const string RejectLowVolume = "REGIME_LOW_VOLUME";
    public const string RejectWrongRegime = "REGIME_WRONG_FOR_STRATEGY";

    public static bool IsSmart(string? strategyId) =>
        string.Equals(strategyId?.Trim(), SmartStrategyId, StringComparison.OrdinalIgnoreCase);

    public static bool IsEma(string? strategyId) =>
        string.Equals(strategyId?.Trim(), EmaStrategyId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which engine a smart bot should run this bar. Null means "sit this one out".
    /// </summary>
    public static string? EngineFor(MarketRegime regime) => regime switch
    {
        MarketRegime.Sideways => RsiStrategyId,   // fade the extremes inside the range
        MarketRegime.Uptrend => EmaStrategyId,    // ride the cross with the trend
        MarketRegime.Downtrend => EmaStrategyId,
        _ => null                                  // Unclear: no edge either way
    };

    /// <summary>
    /// Whether a produced signal is allowed to become a trade.
    /// <paramref name="engineId"/> is the engine that produced it ("rsi"/"ema"),
    /// <paramref name="botStrategyId"/> is what the user selected.
    /// </summary>
    public static bool IsAllowed(
        string? botStrategyId,
        string? engineId,
        string side,
        RegimeSnapshot? regime,
        out string? rejectCode)
    {
        rejectCode = null;

        // Regime governs the SMART strategy only. Every other strategy runs on its own
        // rules and nothing else: RSI is its entry levels plus the zone backtest, EMA is
        // its cross plus its RSI band, the pattern rule is its pattern. Layering an extra
        // filter over them would mean a strategy no longer does what its name says, and
        // "why didn't it enter?" would stop having one answer.
        if (!IsSmart(botStrategyId))
            return true;

        // No regime measured (feature off, or higher-timeframe entry) — nothing to apply.
        if (regime is null)
            return true;

        // Volume can only ever veto, and only when it is actually present.
        if (regime.VolumeAvailable && !regime.VolumeOk)
        {
            rejectCode = RejectLowVolume;
            return false;
        }

        if (regime.Regime == MarketRegime.Unclear)
        {
            rejectCode = regime.Reason == MarketRegimeClassifier.ReasonDeadMarket
                ? RejectDeadMarket
                : RejectUnclear;
            return false;
        }

        // The regime chose the engine, so a mismatch means the market moved between
        // producing and validating the signal — do not trade on stale routing.
        var expected = EngineFor(regime.Regime);
        var actual = IsEma(engineId) ? EmaStrategyId : RsiStrategyId;
        if (expected is null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            rejectCode = RejectWrongRegime;
            return false;
        }

        // Direction must agree with the trend. In a range both sides are legal.
        if (regime.Regime == MarketRegime.Uptrend && side == "Put")
        {
            rejectCode = RejectCounterTrend;
            return false;
        }

        if (regime.Regime == MarketRegime.Downtrend && side == "Call")
        {
            rejectCode = RejectCounterTrend;
            return false;
        }

        return true;
    }
}
