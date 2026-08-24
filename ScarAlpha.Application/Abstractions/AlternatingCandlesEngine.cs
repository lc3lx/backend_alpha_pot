namespace ScarAlpha.Application.Abstractions;

/// <summary>Direction of a single candle.</summary>
public enum CandleDirection
{
    /// <summary>Open and close are equal — no direction. Breaks any pattern.</summary>
    Flat = 0,
    Up,
    Down
}

/// <summary>
/// "Alternating candles" strategy, on the 5-minute timeframe.
///
/// <para>Rule: look at the last <see cref="PatternLength"/> CLOSED candles. If they
/// alternate strictly — up, down, up, down, up — enter the next candle in the SAME
/// direction as the last one.</para>
///
/// <para>Note what that means: the bet is that the alternation BREAKS, not that it
/// continues. A continuation bet would take the opposite side. This is the rule as
/// specified, and it is worth being explicit about because the two readings are
/// exact opposites.</para>
///
/// <para>A flat candle (close == open) has no direction and breaks the pattern rather
/// than being counted as either side.</para>
/// </summary>
public sealed record AlternatingOptions(
    /// <summary>How many closed candles must alternate before entering.</summary>
    int PatternLength = 5,
    /// <summary>5-minute candles.</summary>
    int TimeframeSeconds = 300,
    /// <summary>The trade lasts exactly one candle — the "sixth".</summary>
    int ExpiryCandles = 1,
    /// <summary>
    /// Seconds after the candle closes in which the entry is still valid.
    /// Zero means "use the shared <see cref="RsiEntryLevels.SetupTtlSeconds"/>".
    /// </summary>
    int MaxEntryLagSeconds = 0)
{
    public static AlternatingOptions Default => new();

    /// <summary>The window actually applied — the shared one unless overridden.</summary>
    public int EffectiveEntryLagSeconds =>
        MaxEntryLagSeconds > 0 ? MaxEntryLagSeconds : RsiEntryLevels.SetupTtlSeconds;

    /// <summary>Contract duration in seconds: one candle of this timeframe.</summary>
    public int DurationSeconds => ExpiryCandles * TimeframeSeconds;
}

/// <summary>What the rule saw, so a skip can be explained.</summary>
public sealed record AlternatingEvaluation(
    string Signal,
    IReadOnlyList<CandleDirection> Pattern,
    CandleDirection LastDirection,
    string? SkipReason);

public static class AlternatingCandlesEngine
{
    public const string ReasonNotAlternating = "NOT_ALTERNATING";
    public const string ReasonFlatCandle = "FLAT_CANDLE";
    public const string ReasonNoDirection = "NO_CANDLE_DIRECTION";
    public const string ReasonInsufficientData = "INSUFFICIENT_DATA";

    /// <summary>
    /// Direction of one candle. Needs a real open — a bar synthesised from a single
    /// quote has none, and guessing from the close would invent a direction.
    /// </summary>
    public static CandleDirection DirectionOf(RsiCandle candle)
    {
        if (candle.Open is not decimal open) return CandleDirection.Flat;
        if (candle.Close > open) return CandleDirection.Up;
        if (candle.Close < open) return CandleDirection.Down;
        return CandleDirection.Flat;
    }

    /// <summary>
    /// Evaluates the rule against a gap-free CLOSED series ending at the most recent
    /// closed candle.
    /// </summary>
    public static AlternatingEvaluation Evaluate(
        IReadOnlyList<RsiCandle> closedBars,
        AlternatingOptions options)
    {
        if (closedBars is null) throw new ArgumentNullException(nameof(closedBars));
        if (options.PatternLength < 2)
            throw new ArgumentOutOfRangeException(nameof(options.PatternLength));

        if (closedBars.Count < options.PatternLength)
            return new AlternatingEvaluation("None", Array.Empty<CandleDirection>(),
                CandleDirection.Flat, ReasonInsufficientData);

        var pattern = new CandleDirection[options.PatternLength];
        for (var i = 0; i < options.PatternLength; i++)
            pattern[i] = DirectionOf(closedBars[closedBars.Count - options.PatternLength + i]);

        // Any flat candle means the sequence is not a clean alternation.
        foreach (var direction in pattern)
        {
            if (direction == CandleDirection.Flat)
                return new AlternatingEvaluation("None", pattern, pattern[^1], ReasonFlatCandle);
        }

        for (var i = 1; i < pattern.Length; i++)
        {
            if (pattern[i] == pattern[i - 1])
                return new AlternatingEvaluation("None", pattern, pattern[^1], ReasonNotAlternating);
        }

        // Enter the next candle the same way the last one closed.
        var last = pattern[^1];
        var signal = last switch
        {
            CandleDirection.Up => "Call",
            CandleDirection.Down => "Put",
            _ => "None"
        };

        return new AlternatingEvaluation(
            signal,
            pattern,
            last,
            signal == "None" ? ReasonNoDirection : null);
    }
}

/// <summary>Identity and timeframe of the alternating-candles strategy.</summary>
public static class AlternatingStrategy
{
    public const string Id = "alt5";
    public const int TimeframeSeconds = 300;

    public static bool Is(string? strategyId) =>
        string.Equals(strategyId?.Trim(), Id, StringComparison.OrdinalIgnoreCase);
}

public interface IAlternatingSignalService
{
    /// <summary>
    /// Alternating-candles rule on CLOSED 5-minute bars. Enters the next candle in the
    /// same direction as the last one once the pattern is complete.
    /// </summary>
    Task<StrategySignal> GetSignalAsync(
        Guid userId,
        string asset,
        IReadOnlyList<RsiCandle> candles,
        AlternatingOptions options,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Records that this pattern was consumed, so it cannot open a second trade.</summary>
    void MarkSignalEmitted(Guid userId, string asset, int timeframeSeconds, DateTimeOffset candleTime);
}

/// <summary>
/// Which chart timeframe each strategy analyses. Most run on 1-minute candles; the
/// alternating-candles rule is defined on 5-minute ones.
/// </summary>
public static class StrategyTimeframes
{
    public const int DefaultSeconds = 60;

    public static int For(string? strategyId) =>
        AlternatingStrategy.Is(strategyId) ? AlternatingStrategy.TimeframeSeconds : DefaultSeconds;
}
