using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Common;

/// <summary>
/// A Running trade only blocks new bot entries until expiry + grace.
/// Stuck 00:00 "Running" rows must not freeze the bot forever.
/// </summary>
public static class OpenTradeGate
{
    public const int RunningGraceSeconds = 90;
    public const int PendingMaxMinutes = 2;

    public static bool IsBlocking(Trade trade, DateTimeOffset now)
    {
        if (trade.Status == TradeStatus.Pending)
            return trade.CreatedAt.AddMinutes(PendingMaxMinutes) > now;

        if (trade.Status != TradeStatus.Running)
            return false;

        var duration = trade.DurationSeconds > 0 ? trade.DurationSeconds : 60;
        return trade.CreatedAt.AddSeconds(duration + RunningGraceSeconds) > now;
    }
}
