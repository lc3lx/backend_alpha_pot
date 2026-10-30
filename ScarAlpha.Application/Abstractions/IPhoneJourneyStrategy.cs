using System.Collections.Concurrent;

namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// "iPhone Journey" — the capital-first configuration run on the operator's own demo
/// account: a short-period RSI extreme on the closed 1-minute bar, a five-minute option,
/// a flat stake, and hard limits that stop the run instead of letting it chase.
///
/// The rule fires on the FIRST of three extremes, read on closed bars only:
///   RSI(5) at or below 15 / at or above 85
///   RSI(7) at or below 20 / at or above 80
///   RSI(7) at or below 15 / at or above 85
/// Oversold is a Call, overbought a Put. One rule, not a vote: the individual extremes
/// are rare, and the union fills a session without loosening any threshold.
///
/// What the measurement says, so nobody builds expectations this does not support: over
/// 699 live demo trades the rule won 51.03% (95% CI 47.3-54.8) at a realised 91.5%
/// payout, whose breakeven is 52.2%. It has no demonstrated edge. Everything else in this
/// class exists to keep that fact from costing more than the user chose to risk — the
/// flat stake, the payout floor, the loss-streak pause, and the session target and loss
/// limit enforced by the bot runtime.
/// </summary>
public static class IPhoneJourneyStrategy
{
    public const string Id = "iphone_journey";

    /// <summary>Five 1-minute bars: the expiry every live measurement used.</summary>
    public const int ExpiryCandles = 5;

    /// <summary>
    /// A signal is only taken this soon after its bar closed. Later than that, the entry
    /// price has drifted from the close the rule was measured at.
    /// </summary>
    public const int EntryWindowSeconds = 25;

    /// <summary>
    /// Breakeven is 1/(1+payout): 52.6% at 90%, 54.1% at 85%. Below 90% the bar rises
    /// faster than any extra pairs could pay for.
    /// </summary>
    public const int MinPayoutPercent = 90;

    /// <summary>Flat stake bounds. No progression of any kind is applied to this strategy.</summary>
    public const decimal MinStake = 1m;
    public const decimal MaxStake = 2m;

    /// <summary>Consecutive losses that trigger a pause, and its length.</summary>
    public const int PauseAfterLosses = 3;
    public static readonly TimeSpan PauseLength = TimeSpan.FromMinutes(15);

    /// <summary>Contiguous closed bars needed before RSI(7) is stable enough to read.</summary>
    public const int RequiredCandles = 30;

    public static bool Is(string? strategyId) =>
        string.Equals(strategyId?.Trim(), Id, StringComparison.OrdinalIgnoreCase);

    public static decimal ClampStake(decimal amount) =>
        Math.Clamp(amount <= 0 ? MinStake : amount, MinStake, MaxStake);

    /// <summary>
    /// Direction from the closed series: ("Call" | "Put" | "None", strength), where
    /// strength is how many RSI points past its threshold the firing reading was.
    /// </summary>
    public static (string Signal, decimal Rsi, decimal Strength) Evaluate(IReadOnlyList<decimal> closes)
    {
        if (closes is null || closes.Count < RequiredCandles)
            return ("None", 0m, 0m);

        var rsi5 = Indicators.Rsi(closes, 5);
        var rsi7 = Indicators.Rsi(closes, 7);
        if (rsi5 is null || rsi7 is null)
            return ("None", 0m, 0m);

        (int Length, decimal Value, decimal Low, decimal High)[] readings =
        [
            (5, rsi5.Value, 15m, 85m),
            (7, rsi7.Value, 20m, 80m),
            (7, rsi7.Value, 15m, 85m),
        ];

        foreach (var (_, value, low, high) in readings)
        {
            if (value <= low) return ("Call", Math.Round(value, 2), Math.Round(low - value, 2));
            if (value >= high) return ("Put", Math.Round(value, 2), Math.Round(value - high, 2));
        }
        return ("None", Math.Round(rsi7.Value, 2), 0m);
    }

    /// <summary>
    /// The strategy's signal for the last CLOSED bar, already rejected when its entry
    /// window has passed. <paramref name="lastClosedStart"/> is the bar's open time.
    /// </summary>
    public static StrategySignal BuildSignal(
        string asset,
        IReadOnlyList<decimal> closes,
        DateTimeOffset lastClosedStart,
        int periodSeconds,
        DateTimeOffset now)
    {
        var (side, rsi, _) = Evaluate(closes);
        var closeTime = lastClosedStart + TimeSpan.FromSeconds(periodSeconds);
        var age = (now - closeTime).TotalSeconds;

        string? error = null;
        if (side != "None" && (age < 0 || age > EntryWindowSeconds))
        {
            error = "SETUP_EXPIRED";
            side = "None";
        }

        return new StrategySignal(
            StrategyId: Id,
            Asset: asset.Trim(),
            Signal: side,
            Rsi: rsi,
            CandleTime: closeTime,
            Timeframe: periodSeconds.ToString(),
            // No zone backtest: the stats slot carries the expiry so the executor derives
            // a five-minute contract the same way it does for the other engines.
            Backtest: new RsiBacktestStats(
                TotalSignals: 0,
                SuccessfulSignals: 0,
                FailedSignals: 0,
                SuccessRate: 0m,
                LookbackCandles: 0,
                ExpiryCandles: ExpiryCandles,
                MinimumSuccessRate: 0m,
                Passed: true),
            AutomationError: error,
            LiveRsi: rsi);
    }

    // ---- loss-streak pause -------------------------------------------------------------
    //
    // Measured over 5,054 trades, a run of losses does not predict the next result, so
    // this is not a forecast. It caps how fast an unattended account can be drawn down.
    // Held in memory: after a restart the streak starts again from zero.

    private static readonly ConcurrentDictionary<Guid, StreakState> Streaks = new();

    private sealed record StreakState(int Losses, DateTimeOffset PausedUntil);

    public static void RecordOutcome(Guid userId, bool? won, DateTimeOffset now)
    {
        if (won is null) return;                       // a tie carries no direction
        Streaks.AddOrUpdate(
            userId,
            _ => won.Value
                ? new StreakState(0, DateTimeOffset.MinValue)
                : new StreakState(1, DateTimeOffset.MinValue),
            (_, s) =>
            {
                if (won.Value) return s with { Losses = 0 };
                var losses = s.Losses + 1;
                return losses >= PauseAfterLosses
                    ? new StreakState(0, now + PauseLength)
                    : s with { Losses = losses };
            });
    }

    public static bool IsPaused(Guid userId, DateTimeOffset now, out DateTimeOffset until)
    {
        until = DateTimeOffset.MinValue;
        if (!Streaks.TryGetValue(userId, out var s)) return false;
        until = s.PausedUntil;
        return now < s.PausedUntil;
    }

    public static void Reset(Guid userId) => Streaks.TryRemove(userId, out _);
}
