using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Common;

/// <summary>
/// Enforces allowed Trade status transitions. Final PnL can still settle Unknown/Failed
/// when Binolla close arrives after a wait timeout.
/// </summary>
public static class TradeStateMachine
{
    private static readonly HashSet<TradeStatus> SoftTerminal =
    [
        TradeStatus.Failed,
        TradeStatus.Unknown
    ];

    private static readonly HashSet<TradeStatus> HardTerminal =
    [
        TradeStatus.Profit,
        TradeStatus.Loss,
        TradeStatus.Tie,
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

    /// <summary>Open / not yet finally settled (still eligible for Binolla PnL).</summary>
    public static bool IsOpen(TradeStatus status) =>
        status is TradeStatus.Pending or TradeStatus.Running or TradeStatus.Unknown;

    /// <summary>True when no further outcome updates should apply (except late settle via TryApplyFinalOutcome).</summary>
    public static bool IsTerminal(TradeStatus status) =>
        HardTerminal.Contains(status) || SoftTerminal.Contains(status);

    public static bool IsHardTerminal(TradeStatus status) => HardTerminal.Contains(status);

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

    /// <summary>
    /// Apply Win/Loss/Tie from Binolla — allowed from Running/Pending and from Unknown/Failed
    /// after a timed-out wait (late close).
    /// </summary>
    public static bool TryApplyFinalOutcome(ref TradeStatus current, TradeStatus next)
    {
        if (next is not (TradeStatus.Profit or TradeStatus.Loss or TradeStatus.Tie))
            return TryTransition(ref current, next);

        if (current == next)
            return false;

        if (IsHardTerminal(current))
            return false;

        if (current is TradeStatus.Pending or TradeStatus.Running or TradeStatus.Unknown or TradeStatus.Failed)
        {
            current = next;
            return true;
        }

        throw new InvalidOperationException($"Illegal trade transition {current} → {next}.");
    }
}
