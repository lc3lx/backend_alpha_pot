using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Common;

/// <summary>
/// Enforces allowed Trade status transitions. Outcome re-application to terminal states is a no-op.
/// </summary>
public static class TradeStateMachine
{
    private static readonly HashSet<TradeStatus> Terminal =
    [
        TradeStatus.Profit,
        TradeStatus.Loss,
        TradeStatus.Tie,
        TradeStatus.Failed,
        TradeStatus.Unknown,
        TradeStatus.Cancelled
    ];

    private static readonly Dictionary<TradeStatus, HashSet<TradeStatus>> Allowed = new()
    {
        [TradeStatus.Pending] = [TradeStatus.Running, TradeStatus.Failed, TradeStatus.Cancelled],
        [TradeStatus.Running] =
        [
            TradeStatus.Profit,
            TradeStatus.Loss,
            TradeStatus.Tie,
            TradeStatus.Failed,
            TradeStatus.Unknown
        ]
    };

    public static bool IsTerminal(TradeStatus status) => Terminal.Contains(status);

    /// <summary>
    /// Returns true if the transition was applied; false if already terminal (idempotent ignore).
    /// Throws if an illegal transition is attempted from a non-terminal state.
    /// </summary>
    public static bool TryTransition(ref TradeStatus current, TradeStatus next)
    {
        if (current == next)
            return false;

        if (IsTerminal(current))
            return false;

        if (!Allowed.TryGetValue(current, out var nexts) || !nexts.Contains(next))
            throw new InvalidOperationException($"Illegal trade transition {current} → {next}.");

        current = next;
        return true;
    }
}
