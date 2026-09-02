using System.Collections.Concurrent;

namespace ScarAlpha.Application.Common;

/// <summary>
/// Benches a pair after the bot loses on it.
///
/// <para>A losing entry usually means the pair is not behaving the way the strategy
/// assumes right now — a range breaking out under a mean-reversion rule, say. Re-entering
/// the same pair on the next bar tends to lose the same way, so it sits out for a while
/// and the bot spends that time on the rest of the list.</para>
///
/// <para><b>Scope is (strategy, pair), NOT (user, pair).</b> Everyone in a cohort takes
/// the same entry at the same instant and therefore shares the outcome, so a per-user
/// cooldown would hold the identical value for everyone — until it drifted, and then one
/// account would skip a pair its neighbour still traded. Keeping it strategy-wide means
/// the benched list is the same for every user, which is what keeps their trades
/// identical.</para>
/// </summary>
public static class PairCooldownRegistry
{
    /// <summary>Default bench time after a loss.</summary>
    public const int DefaultCooldownSeconds = 3600;

    private static int _cooldownSeconds = DefaultCooldownSeconds;

    /// <summary>
    /// Tunable via <c>Strategy:PairLossCooldownSeconds</c>. Zero disables the rule.
    /// </summary>
    public static int CooldownSeconds
    {
        get => _cooldownSeconds;
        set => _cooldownSeconds = value is >= 0 and <= 86400
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Cooldown must be 0-86400 seconds.");
    }

    private static readonly ConcurrentDictionary<string, DateTimeOffset> BenchedUntil =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Benches <paramref name="asset"/> for this strategy, starting at <paramref name="now"/>.</summary>
    public static void RecordLoss(string? strategyId, string asset, DateTimeOffset now)
    {
        if (CooldownSeconds <= 0) return;
        if (string.IsNullOrWhiteSpace(asset)) return;

        var until = now.AddSeconds(CooldownSeconds);
        // Extend, never shorten: a later loss must not be able to end an earlier bench.
        BenchedUntil.AddOrUpdate(Key(strategyId, asset), until, (_, existing) => existing > until ? existing : until);
    }

    /// <summary>True while the pair is still serving a cooldown for this strategy.</summary>
    public static bool IsBenched(string? strategyId, string asset, DateTimeOffset now)
    {
        if (CooldownSeconds <= 0) return false;
        if (string.IsNullOrWhiteSpace(asset)) return false;
        return BenchedUntil.TryGetValue(Key(strategyId, asset), out var until) && until > now;
    }

    /// <summary>When the pair is free again, or null when it is not benched.</summary>
    public static DateTimeOffset? BenchedUntilTime(string? strategyId, string asset, DateTimeOffset now)
    {
        if (!IsBenched(strategyId, asset, now)) return null;
        return BenchedUntil.TryGetValue(Key(strategyId, asset), out var until) ? until : null;
    }

    /// <summary>Drops expired entries. Safe to call on a timer.</summary>
    public static void Evict(DateTimeOffset now)
    {
        foreach (var (key, until) in BenchedUntil)
        {
            if (until <= now)
                BenchedUntil.TryRemove(key, out _);
        }
    }

    /// <summary>Clears every bench — tests only.</summary>
    public static void Clear() => BenchedUntil.Clear();

    /// <summary>
    /// The strategy a bot trade was placed under, read back from its idempotency key
    /// (<c>bot:{strategy}:{asset}:{bar}:{direction}</c>). The Trade row does not store the
    /// strategy, and the key is already the authoritative record of it.
    /// </summary>
    public static string? StrategyFromBotKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        if (!idempotencyKey.StartsWith("bot:", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = idempotencyKey.Split(':');
        return parts.Length >= 2 && parts[1].Length > 0 ? parts[1] : null;
    }

    private static string Key(string? strategyId, string asset) =>
        $"{(strategyId ?? "rsi").Trim().ToLowerInvariant()}:{asset.Trim().ToUpperInvariant()}";
}
