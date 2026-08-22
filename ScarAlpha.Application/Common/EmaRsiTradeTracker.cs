using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Application.Common;

/// <summary>
/// The Pine script's scoring half, ported so live numbers can be compared with the
/// TradingView backtest.
///
/// Pine:
///   entry price   = close of the signal bar
///   checkpoint    = after checkpointBars bars, is it winning yet?
///   settlement    = after tradeDurationBars bars, close vs entry close
///                   long wins when close &gt; entryClose, short when close &lt; entryClose
///
/// This is deliberately the script's own close-vs-close rule, not the broker's payout.
/// Keeping it separate is what makes the comparison meaningful: if these counters drift
/// from the broker's settled results, the difference is Binolla's fill/expiry pricing,
/// not the strategy.
/// </summary>
public sealed class EmaRsiTradeTracker
{
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<Guid, Counters> _counters = new();

    private sealed record Pending(
        string Side,
        decimal EntryClose,
        DateTimeOffset EntryBarClose,
        DateTimeOffset CheckpointAt,
        DateTimeOffset SettlesAt,
        bool CheckpointDone);

    private sealed class Counters
    {
        public int ManualWins;
        public int ManualLosses;
        public int CheckpointWins;
        public int CheckpointLosses;
    }

    /// <summary>Called when a trade is actually placed on an EMA signal.</summary>
    public void RecordEntry(
        Guid userId,
        string asset,
        string side,
        decimal entryClose,
        DateTimeOffset entryBarClose,
        int checkpointBars,
        int durationBars,
        int timeframeSeconds = 60)
    {
        if (string.IsNullOrWhiteSpace(asset)) return;
        if (side is not ("Call" or "Put")) return;

        var bar = TimeSpan.FromSeconds(timeframeSeconds);
        _pending[Key(userId, asset)] = new Pending(
            Side: side,
            EntryClose: entryClose,
            EntryBarClose: entryBarClose,
            CheckpointAt: entryBarClose + bar * checkpointBars,
            SettlesAt: entryBarClose + bar * durationBars,
            CheckpointDone: false);
    }

    /// <summary>
    /// Advances any pending entry for this pair using the closed series we already have.
    /// Safe to call on every poll — each milestone is scored at most once.
    /// </summary>
    public void Resolve(Guid userId, string asset, IReadOnlyList<RsiCandle> closed, int timeframeSeconds = 60)
    {
        if (string.IsNullOrWhiteSpace(asset) || closed is null || closed.Count == 0) return;

        var key = Key(userId, asset);
        if (!_pending.TryGetValue(key, out var pending)) return;

        var bar = TimeSpan.FromSeconds(timeframeSeconds);
        var counters = _counters.GetOrAdd(userId, _ => new Counters());

        if (!pending.CheckpointDone &&
            TryFindClose(closed, pending.CheckpointAt, bar, out var checkpointClose))
        {
            if (IsWin(pending.Side, pending.EntryClose, checkpointClose))
                Interlocked.Increment(ref counters.CheckpointWins);
            else
                Interlocked.Increment(ref counters.CheckpointLosses);

            pending = pending with { CheckpointDone = true };
            _pending[key] = pending;
        }

        if (TryFindClose(closed, pending.SettlesAt, bar, out var settleClose))
        {
            if (IsWin(pending.Side, pending.EntryClose, settleClose))
                Interlocked.Increment(ref counters.ManualWins);
            else
                Interlocked.Increment(ref counters.ManualLosses);

            _pending.TryRemove(key, out _);
        }
    }

    public EmaRsiStats GetStats(Guid userId)
    {
        if (!_counters.TryGetValue(userId, out var c))
            return new EmaRsiStats(0, 0, 0, 0, 0m);

        var total = c.ManualWins + c.ManualLosses;
        var rate = total == 0
            ? 0m
            : Math.Round(c.ManualWins * 100m / total, 2, MidpointRounding.AwayFromZero);
        return new EmaRsiStats(c.ManualWins, c.ManualLosses, c.CheckpointWins, c.CheckpointLosses, rate);
    }

    public void Reset(Guid userId)
    {
        _counters.TryRemove(userId, out _);
        foreach (var key in _pending.Keys.Where(k => k.StartsWith($"{userId:N}:", StringComparison.Ordinal)).ToList())
            _pending.TryRemove(key, out _);
    }

    /// <summary>Pine: long wins on a higher close, short on a lower one. Equal is a loss.</summary>
    private static bool IsWin(string side, decimal entryClose, decimal exitClose) =>
        side == "Call" ? exitClose > entryClose : exitClose < entryClose;

    /// <summary>Finds the bar that closes exactly at <paramref name="closeInstant"/>, if it exists yet.</summary>
    private static bool TryFindClose(
        IReadOnlyList<RsiCandle> closed,
        DateTimeOffset closeInstant,
        TimeSpan bar,
        out decimal close)
    {
        close = 0m;
        for (var i = closed.Count - 1; i >= 0; i--)
        {
            var candleClose = closed[i].Timestamp + bar;
            if (candleClose == closeInstant)
            {
                close = closed[i].Close;
                return true;
            }
            if (candleClose < closeInstant)
                return false;
        }

        return false;
    }

    private static string Key(Guid userId, string asset) =>
        $"{userId:N}:{asset.Trim().ToUpperInvariant()}";
}

/// <summary>Live equivalent of the Pine corner table.</summary>
public sealed record EmaRsiStats(
    int Wins,
    int Losses,
    int CheckpointWins,
    int CheckpointLosses,
    decimal WinRate);
