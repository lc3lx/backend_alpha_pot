using System.Collections.Concurrent;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Common;

/// <summary>
/// A Running trade only blocks new bot entries until expiry + a short settle.
/// 90s used to swallow the next 1-minute bar's entire 30s entry window after a
/// 5-minute option, so a live PUT/CALL on the next close was shown and never taken.
/// </summary>
public static class OpenTradeGate
{
    public const int RunningGraceSeconds = 8;
    public const int PendingMaxMinutes = 2;

    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> HeldUntil = new();
    private static readonly ConcurrentDictionary<Guid, long> LastSkipLogMs = new();

    public static bool IsBlocking(Trade trade, DateTimeOffset now)
    {
        if (trade.Status == TradeStatus.Pending)
            return trade.CreatedAt.AddMinutes(PendingMaxMinutes) > now;

        if (trade.Status != TradeStatus.Running)
            return false;

        var duration = trade.DurationSeconds > 0 ? trade.DurationSeconds : 60;
        return trade.CreatedAt.AddSeconds(duration + RunningGraceSeconds) > now;
    }

    public static void MarkUserHeld(Guid userId, int durationSeconds)
    {
        var until = HoldUntil(durationSeconds);
        HeldUntil.AddOrUpdate(userId, until, (_, existing) => existing > until ? existing : until);
    }

    /// <summary>
    /// One open trade per account. A second caller loses until this hold ends,
    /// so two pairs cannot be sent in the same moment.
    /// </summary>
    public static bool TryHold(Guid userId, int durationSeconds)
    {
        var until = HoldUntil(durationSeconds);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (IsUserHeld(userId))
                return false;
            if (HeldUntil.TryAdd(userId, until))
                return true;
            if (HeldUntil.TryGetValue(userId, out var existing) && existing <= DateTimeOffset.UtcNow)
                HeldUntil.TryRemove(userId, out _);
        }

        return false;
    }

    private static DateTimeOffset HoldUntil(int durationSeconds)
    {
        var holdSec = Math.Max(durationSeconds, 60) + RunningGraceSeconds;
        return DateTimeOffset.UtcNow.AddSeconds(holdSec);
    }

    public static void ReleaseUser(Guid userId) => HeldUntil.TryRemove(userId, out _);

    public static bool IsUserHeld(Guid userId) =>
        HeldUntil.TryGetValue(userId, out var until) && until > DateTimeOffset.UtcNow;

    public static bool ShouldLogSkip(Guid userId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var last = LastSkipLogMs.AddOrUpdate(userId, now, (_, prev) => now - prev >= 2000 ? now : prev);
        return last == now;
    }
}
