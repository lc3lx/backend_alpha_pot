namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// What kind of market a pair is in right now. <see cref="Unclear"/> is 0 so the
/// default value means "do not trade" rather than something tradable.
/// </summary>
public enum MarketRegime
{
    Unclear = 0,
    Uptrend,
    Downtrend,
    Sideways
}

public sealed record MarketRegimeOptions(
    int AdxLength = 14,
    int AtrLength = 14,
    /// <summary>ADX at or above this is a real trend.</summary>
    decimal TrendAdxMin = 25m,
    /// <summary>ADX at or below this is a range. The gap to TrendAdxMin is deliberate.</summary>
    decimal RangeAdxMax = 20m,
    /// <summary>+DI and -DI must differ by at least this much to call a direction.</summary>
    decimal DiSeparationMin = 2m,
    int AtrRatioLookback = 100,
    /// <summary>Current ATR must be at least this fraction of its own long average.</summary>
    decimal MinAtrRatio = 0.6m,
    /// <summary>Absolute floor in basis points, for pairs that were dead all lookback.</summary>
    decimal MinAtrBps = 0.5m,
    int VolumeLookback = 20,
    decimal MinRelativeVolume = 0.8m,
    bool VolumeFilterEnabled = true)
{
    public static MarketRegimeOptions Default => new();
}

/// <summary>Already-computed scalars, so classification can be table-tested on its own.</summary>
public readonly record struct RegimeInputs(
    decimal Adx,
    decimal PlusDi,
    decimal MinusDi,
    decimal AtrRatio,
    decimal AtrBps,
    decimal? RelativeVolume,
    bool HasEnoughBars);

/// <summary>Everything the classifier looked at, so a skip can always be explained.</summary>
public sealed record RegimeSnapshot(
    MarketRegime Regime,
    decimal Adx,
    decimal PlusDi,
    decimal MinusDi,
    decimal Atr,
    decimal AtrRatio,
    decimal AtrBps,
    decimal? RelativeVolume,
    bool VolumeAvailable,
    bool VolumeOk,
    string? Reason)
{
    public static RegimeSnapshot None(string reason) =>
        new(MarketRegime.Unclear, 0m, 0m, 0m, 0m, 0m, 0m, null, false, true, reason);

    /// <summary>Uptrend and Downtrend only — Sideways has no direction.</summary>
    public bool IsTrending => Regime is MarketRegime.Uptrend or MarketRegime.Downtrend;
}

/// <summary>
/// Decides whether a pair is trending, ranging, or not worth touching.
///
/// <para>Built on price (ADX/ATR), never on volume. Binolla leaves volume empty on every
/// forming bar and may never send it at all, so volume can only ever veto — it can never
/// be the reason a regime is identified.</para>
///
/// <para>Pure: no clock, no I/O. That is what lets the result live inside the shared
/// per-bar analysis and be identical for every account.</para>
/// </summary>
public static class MarketRegimeClassifier
{
    public const string ReasonInsufficientData = "INSUFFICIENT_DATA";
    public const string ReasonDeadMarket = "DEAD_MARKET";
    public const string ReasonDiAmbiguous = "DI_AMBIGUOUS";
    public const string ReasonAdxBand = "ADX_BAND";

    /// <summary>Classification over pre-computed scalars.</summary>
    public static MarketRegime Classify(RegimeInputs input, MarketRegimeOptions options, out string? reason)
    {
        if (!input.HasEnoughBars)
        {
            reason = ReasonInsufficientData;
            return MarketRegime.Unclear;
        }

        // A market that is barely moving cannot pay a binary-option spread, and RSI
        // extremes on a flat tape are noise. Unclear, deliberately NOT Sideways.
        if (input.AtrRatio < options.MinAtrRatio || input.AtrBps < options.MinAtrBps)
        {
            reason = ReasonDeadMarket;
            return MarketRegime.Unclear;
        }

        if (input.Adx >= options.TrendAdxMin)
        {
            var separation = input.PlusDi - input.MinusDi;
            if (separation >= options.DiSeparationMin)
            {
                reason = null;
                return MarketRegime.Uptrend;
            }

            if (-separation >= options.DiSeparationMin)
            {
                reason = null;
                return MarketRegime.Downtrend;
            }

            reason = ReasonDiAmbiguous;
            return MarketRegime.Unclear;
        }

        if (input.Adx <= options.RangeAdxMax)
        {
            reason = null;
            return MarketRegime.Sideways;
        }

        // Between RangeAdxMax and TrendAdxMin: too weak to trade with, too strong to fade.
        // The band exists so the regime does not flip every bar around a single threshold.
        reason = ReasonAdxBand;
        return MarketRegime.Unclear;
    }

    /// <summary>
    /// Full analysis over a gap-free CLOSED series (see <see cref="CandleSeries.Prepare"/>).
    /// </summary>
    public static RegimeSnapshot Analyze(IReadOnlyList<RsiCandle> closedBars, MarketRegimeOptions options)
    {
        if (closedBars is null || closedBars.Count == 0)
            return RegimeSnapshot.None(ReasonInsufficientData);

        var needed = Math.Max(
            IndicatorWarmup.ForAdx(options.AdxLength),
            IndicatorWarmup.ForAtr(options.AtrLength));
        if (closedBars.Count < needed)
            return RegimeSnapshot.None(ReasonInsufficientData);

        var adx = Volatility.Adx(closedBars, options.AdxLength);
        var atrSeries = Volatility.AtrSeries(closedBars, options.AtrLength);
        var atr = atrSeries[^1];
        if (adx is null)
            return RegimeSnapshot.None(ReasonInsufficientData);

        var atrRatio = AverageRatio(atrSeries, atr, options.AtrLength, options.AtrRatioLookback);
        var lastClose = closedBars[^1].Close;
        var atrBps = lastClose > 0m
            ? Math.Round(atr * 10_000m / lastClose, 4, MidpointRounding.AwayFromZero)
            : 0m;

        var relativeVolume = Volatility.RelativeVolume(closedBars, options.VolumeLookback);
        var volumeAvailable = relativeVolume is not null;
        // Missing volume is "cannot tell", so the filter passes. It only ever vetoes.
        var volumeOk = !options.VolumeFilterEnabled
                       || !volumeAvailable
                       || relativeVolume!.Value >= options.MinRelativeVolume;

        var inputs = new RegimeInputs(
            Adx: adx.Value.Adx,
            PlusDi: adx.Value.PlusDi,
            MinusDi: adx.Value.MinusDi,
            AtrRatio: atrRatio,
            AtrBps: atrBps,
            RelativeVolume: relativeVolume,
            HasEnoughBars: true);

        var regime = Classify(inputs, options, out var reason);

        return new RegimeSnapshot(
            Regime: regime,
            Adx: Math.Round(adx.Value.Adx, 4, MidpointRounding.AwayFromZero),
            PlusDi: Math.Round(adx.Value.PlusDi, 4, MidpointRounding.AwayFromZero),
            MinusDi: Math.Round(adx.Value.MinusDi, 4, MidpointRounding.AwayFromZero),
            Atr: atr,
            AtrRatio: atrRatio,
            AtrBps: atrBps,
            RelativeVolume: relativeVolume,
            VolumeAvailable: volumeAvailable,
            VolumeOk: volumeOk,
            Reason: reason);
    }

    /// <summary>Current ATR against its own recent average — self-normalising across pairs.</summary>
    private static decimal AverageRatio(decimal[] atrSeries, decimal current, int atrLength, int lookback)
    {
        if (current <= 0m) return 0m;

        var last = atrSeries.Length - 1;
        var first = Math.Max(atrLength, last - lookback + 1);
        if (first > last) return 0m;

        decimal sum = 0m;
        var count = 0;
        for (var i = first; i <= last; i++)
        {
            if (atrSeries[i] <= 0m) continue;
            sum += atrSeries[i];
            count++;
        }

        if (count == 0) return 0m;
        var average = sum / count;
        return average <= 0m
            ? 0m
            : Math.Round(current / average, 4, MidpointRounding.AwayFromZero);
    }
}
