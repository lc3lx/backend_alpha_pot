using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Application.Common;

/// <summary>
/// Identifies a group of bots whose trading decision must be identical.
///
/// Two bots that run the same strategy for the same trade duration derive the same
/// <see cref="RsiStrategyOptions"/> from the same market data, so there is no honest
/// reason for them to reach different conclusions — and every observed divergence came
/// from WHEN each user happened to be scanned, not from what the market did.
/// </summary>
public readonly record struct SignalCohort(string StrategyId, int DurationSeconds)
{
    public static SignalCohort For(string? strategyId, int durationSeconds) =>
        new((strategyId ?? "rsi").Trim().ToLowerInvariant(), durationSeconds);

    public override string ToString() => $"{StrategyId}:{DurationSeconds}";
}

/// <summary>
/// One ranked decision per cohort per closed bar, shared by every user in that cohort.
/// </summary>
/// <param name="Cohort">Who this decision belongs to.</param>
/// <param name="ClosedBarTime">The bar the decision was taken on.</param>
/// <param name="Candidates">
/// Tradable setups, best first. Every user in the cohort walks this same list in this
/// same order, so with identical pair selections they all place the identical trade.
/// </param>
/// <param name="AssetsScanned">How many pairs produced a usable analysis.</param>
public sealed record CohortDecision(
    SignalCohort Cohort,
    DateTimeOffset ClosedBarTime,
    IReadOnlyList<CohortCandidate> Candidates,
    int AssetsScanned);

/// <param name="Asset">Pair the setup is on.</param>
/// <param name="Signal">The account-independent signal, computed once.</param>
public sealed record CohortCandidate(string Asset, StrategySignal Signal);

/// <summary>
/// Central decision store: the scan runs ONCE per cohort per bar and every user reads
/// the result, instead of each user scanning the market on their own clock.
///
/// <para>Why this exists. Bots used to be evaluated per user, and with several accounts
/// running the market moved between the first user's scan and the last one's: an entry
/// that was live for user #1 had aged past its TTL by the time user #7 was reached, so
/// one account traded and another did not. Caching the *market analysis*
/// (<see cref="MarketAnalysisCache"/>) fixed the data but not the timing — the decision
/// was still taken separately per user. This caches the DECISION.</para>
///
/// <para>Per-user state deliberately stays out of here: open-trade holds, daily limits,
/// stake size and pair selection are still applied by each user's own pipeline. This
/// only guarantees everyone is choosing from one list, produced at one instant.</para>
/// </summary>
public sealed class CohortSignalCache
{
    /// <summary>Decisions are dropped once they are this many bars old.</summary>
    private const int RetainBars = 3;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public CohortDecision? Value;
    }

    /// <summary>
    /// Returns the cohort's decision for the bar current at <paramref name="now"/>,
    /// producing it once if this is the first caller.
    ///
    /// <paramref name="produce"/> returning null means "could not decide" (no live
    /// session, no warmed-up pair). Nulls are never cached, so the next caller retries;
    /// a decision with zero candidates IS cached, because "nothing to trade on this bar"
    /// is a real answer and re-scanning every second would undo the point of this class.
    /// </summary>
    public async Task<CohortDecision?> GetOrAddAsync(
        SignalCohort cohort,
        int timeframeSeconds,
        DateTimeOffset now,
        Func<DateTimeOffset, CancellationToken, Task<CohortDecision?>> produce,
        CancellationToken ct = default)
    {
        if (timeframeSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeframeSeconds));

        var expectedBar = MarketAnalysisCache.CurrentClosedBarTime(now, timeframeSeconds);
        var entry = _entries.GetOrAdd(cohort.ToString(), _ => new Entry());

        if (IsFresh(entry.Value, expectedBar))
            return entry.Value;

        // Single-flight: one scan per cohort per bar however many users arrive at once.
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh(entry.Value, expectedBar))
                return entry.Value;

            var produced = await produce(expectedBar, ct).ConfigureAwait(false);
            if (produced is null)
                return null;

            // A scan that reached no pair at all is a failed scan, not an empty market.
            if (produced.ClosedBarTime == expectedBar && produced.AssetsScanned > 0)
                entry.Value = produced;

            return produced;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Latest published decision for a cohort, if it is still current.</summary>
    public CohortDecision? TryGet(SignalCohort cohort, int timeframeSeconds, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(cohort.ToString(), out var entry)) return null;
        var expected = MarketAnalysisCache.CurrentClosedBarTime(now, timeframeSeconds);
        return IsFresh(entry.Value, expected) ? entry.Value : null;
    }

    private static bool IsFresh(CohortDecision? value, DateTimeOffset expectedBar) =>
        value is not null && value.ClosedBarTime == expectedBar;

    /// <summary>Drops decisions older than <see cref="RetainBars"/>.</summary>
    public void Evict(DateTimeOffset now, int timeframeSeconds)
    {
        if (timeframeSeconds <= 0) return;
        foreach (var (_, entry) in _entries)
        {
            var value = entry.Value;
            if (value is null) continue;
            if ((now - value.ClosedBarTime).TotalSeconds / timeframeSeconds > RetainBars)
                entry.Value = null;
        }
    }
}
