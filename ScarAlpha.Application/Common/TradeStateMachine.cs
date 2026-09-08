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
    /// Overwrites an already-final outcome with the broker's own record.
    ///
    /// <para>Every other path treats Profit/Loss/Tie as permanent, and that is right: a
    /// settled trade must not drift because of a late or duplicate frame. But it also made
    /// a WRONG outcome permanent — a trade that won on Binolla and was recorded as a loss
    /// stayed a loss forever, and the user's balance and history disagreed with their
    /// broker account with no way back.</para>
    ///
    /// <para>This is the one path allowed to correct that, and only the reconciliation
    /// worker uses it, only with a value read back from Binolla, and only with an audit
    /// entry. It is deliberately separate from <see cref="TryApplyFinalOutcome"/> so no
    /// ordinary code path can reach it by accident.</para>
    /// </summary>
    public static bool TryApplyBrokerCorrection(ref TradeStatus current, TradeStatus next)
    {
        if (next is not (TradeStatus.Profit or TradeStatus.Loss or TradeStatus.Tie))
            return false;
        if (current == next)
            return false;

        // Cancelled trades never reached the broker, so it has nothing to correct.
        if (current == TradeStatus.Cancelled)
            return false;

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
