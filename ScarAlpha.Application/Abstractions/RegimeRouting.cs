namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Which engine may run, and in which direction, given the market regime.
///
/// <para>The rule behind all of it: RSI mean-reversion only pays in a range, and an EMA
/// cross only pays with the trend behind it. Running either in the wrong regime is the
/// main way this bot loses.</para>
///
/// <para>Regime never silently swaps engines under a user who picked one. A bot set to
/// <c>rsi</c> keeps running RSI; the regime just refuses to let it trade where RSI does
/// not work. Only <see cref="SmartStrategyId"/> hands the choice to the regime.</para>
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

        // No regime measured (feature off, or higher-timeframe entry) — behave as before.
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

        var isEmaEngine = IsEma(engineId);

        if (IsSmart(botStrategyId))
        {
            // The regime chose the engine, so a mismatch means the market moved between
            // producing and validating the signal — do not trade on stale routing.
            var expected = EngineFor(regime.Regime);
            if (expected is null || !string.Equals(expected, isEmaEngine ? EmaStrategyId : RsiStrategyId,
                    StringComparison.OrdinalIgnoreCase))
            {
                rejectCode = RejectWrongRegime;
                return false;
            }
        }
        else if (isEmaEngine)
        {
            // EMA is a trend strategy: a range gives its crosses no follow-through.
            if (!regime.IsTrending)
            {
                rejectCode = RejectWrongRegime;
                return false;
            }
        }
        else
        {
            // RSI is mean-reversion: fading a real trend is how it bleeds.
            if (regime.Regime != MarketRegime.Sideways)
            {
                rejectCode = RejectWrongRegime;
                return false;
            }
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
