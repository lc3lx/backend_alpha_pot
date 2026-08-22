using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Application.Common;

/// <summary>
/// One analysis per pair per closed bar — shared by every account.
///
/// Why this exists:
///  * CORRECTNESS. Market data for a pair is the same for everyone. When each user
///    fetched and analyzed independently, two accounts polling the same minute could
///    land on slightly different candle snapshots and disagree on the signal. Now the
///    first caller for a (pair, closed bar) computes the analysis and every other
///    account reuses that exact result, so all accounts act on one decision.
///  * SCALE. Work drops from O(users x pairs) to O(pairs) per minute. With 100 users
///    on 20 pairs that is 20 history fetches a minute instead of 2000.
///
/// Only CLOSED-bar analysis is cached: it is constant for the whole bar, and entries
/// are taken from closed bars. The forming-bar/live overlay stays per session because
/// it changes every tick and is display-only.
/// </summary>
public sealed class MarketAnalysisCache
{
    /// <summary>Analyses are dropped once they are this many bars old.</summary>
    private const int RetainBars = 3;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public MarketAnalysis? Value;
    }

    /// <summary>
    /// Returns the shared analysis for the bar that is current at <paramref name="now"/>,
    /// computing it once via <paramref name="produce"/> if this is the first caller.
    ///
    /// <paramref name="produce"/> must return null when it cannot build a trustworthy
    /// analysis (no live session, not enough warmup) — nulls are not cached, so the next
    /// account through will retry.
    /// </summary>
    public async Task<MarketAnalysis?> GetOrAddAsync(
        string asset,
        int timeframeSeconds,
        DateTimeOffset now,
        Func<CancellationToken, Task<MarketAnalysis?>> produce,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (timeframeSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeframeSeconds));

        var key = Key(asset, timeframeSeconds);
        var expectedBar = CurrentClosedBarTime(now, timeframeSeconds);
        var entry = _entries.GetOrAdd(key, _ => new Entry());

        if (IsFresh(entry.Value, expectedBar))
            return entry.Value;

        // Single-flight: one fetch+compute per pair per bar, no matter how many
        // accounts arrive at the same instant.
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh(entry.Value, expectedBar))
                return entry.Value;

            var produced = await produce(ct).ConfigureAwait(false);
            if (produced is null)
                return null;

            // Only publish an analysis that really belongs to the expected bar;
            // a late/stale fetch must not be pinned for other accounts.
            if (produced.ClosedBarTime == expectedBar)
                entry.Value = produced;

            return produced;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Latest published analysis for a pair, if it is still current.</summary>
    public MarketAnalysis? TryGet(string asset, int timeframeSeconds, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(asset)) return null;
        if (!_entries.TryGetValue(Key(asset, timeframeSeconds), out var entry)) return null;
        var expected = CurrentClosedBarTime(now, timeframeSeconds);
        return IsFresh(entry.Value, expected) ? entry.Value : null;
    }

    private static bool IsFresh(MarketAnalysis? value, DateTimeOffset expectedBar) =>
        value is not null && value.ClosedBarTime == expectedBar;

    /// <summary>
    /// Close instant of the most recently completed bar at <paramref name="now"/>.
    /// At 10:07:03 on a 1m timeframe that is 10:07:00.
    /// </summary>
    public static DateTimeOffset CurrentClosedBarTime(DateTimeOffset now, int timeframeSeconds)
    {
        var unix = now.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(unix / timeframeSeconds * timeframeSeconds);
    }

    /// <summary>Drops analyses older than <see cref="RetainBars"/>.</summary>
    public void Evict(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            var value = entry.Value;
            if (value is null) continue;
            var ageBars = (now - value.ClosedBarTime).TotalSeconds / value.TimeframeSeconds;
            if (ageBars > RetainBars)
                entry.Value = null;
            _ = key;
        }
    }

    private static string Key(string asset, int timeframeSeconds) =>
        $"{asset.Trim().ToUpperInvariant()}:{timeframeSeconds}";
}

/// <summary>
/// The account-independent half of a signal: the closed series for one pair and every
/// indicator derived from it. Per-account state (setup TTL, consumed, open trade,
/// selected pairs) is applied on top of this by each user's own pipeline.
/// </summary>
public sealed record MarketAnalysis(
    string Asset,
    int TimeframeSeconds,
    /// <summary>Close instant of the last closed bar this analysis describes.</summary>
    DateTimeOffset ClosedBarTime,
    IReadOnlyList<RsiCandle> ClosedCandles,
    IReadOnlyList<decimal> Closes,
    decimal Rsi,
    /// <summary>Null on higher-timeframe entries, which exist only to carry closes.</summary>
    RsiBacktestStats? CallBacktest,
    RsiBacktestStats? PutBacktest,
    /// <summary>Market regime for this bar. Null on higher-timeframe entries.</summary>
    RegimeSnapshot? Regime = null)
{
    public int CandleCount => Closes.Count;
}
