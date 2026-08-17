using System.Collections.Concurrent;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Common;

/// <summary>
/// A Running trade only blocks new bot entries until expiry + grace.
/// Stuck 00:00 "Running" rows must not freeze the bot forever.
/// In-memory hold stops parallel scans before they hit history/DB.
/// </summary>
public static class OpenTradeGate
{
    public const int RunningGraceSeconds = 90;
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
        var holdSec = Math.Max(durationSeconds, 60) + RunningGraceSeconds;
        var until = DateTimeOffset.UtcNow.AddSeconds(holdSec);
        HeldUntil.AddOrUpdate(userId, until, (_, existing) => existing > until ? existing : until);
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
