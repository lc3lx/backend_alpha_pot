using System.Collections.Concurrent;
using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Binolla.Session;

internal sealed class PendingOpenOrder
{
    public required int RequestId { get; init; }
    public required string Asset { get; init; }
    public required TradeDirection Direction { get; init; }
    public required decimal Amount { get; init; }
    public required int DurationSeconds { get; init; }
    public required DateTimeOffset PlacedAt { get; init; }
    public required TaskCompletionSource<OrderResponse> Completion { get; init; }
}

/// <summary>
/// Per-session order correlation — replaces single-slot NewOpenOrder/OrderData.
/// </summary>
public sealed class OrderCorrelationHub
{
    private readonly ConcurrentDictionary<int, PendingOpenOrder> _pendingOpens = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TradeOutcome>> _outcomeWaiters =
        new(StringComparer.OrdinalIgnoreCase);

    private int _requestIdSeq;

    public int NextRequestId() => Interlocked.Increment(ref _requestIdSeq);

    public TaskCompletionSource<OrderResponse> RegisterOpen(
        int requestId,
        string asset,
        TradeDirection direction,
        decimal amount,
        int durationSeconds)
    {
        var tcs = new TaskCompletionSource<OrderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingOpenOrder
        {
            RequestId = requestId,
            Asset = asset,
            Direction = direction,
            Amount = amount,
            DurationSeconds = durationSeconds,
            PlacedAt = DateTimeOffset.UtcNow,
            Completion = tcs
        };

        if (!_pendingOpens.TryAdd(requestId, pending))
            throw new InvalidOperationException($"Duplicate open request id {requestId}.");

        return tcs;
    }

    public bool TryCompleteOpenSuccess(OpenedOrderWire wire, AccountType accountType, out OrderResponse? response)
    {
        response = null;
        var deal = wire.Deal;
        if (deal is null || string.IsNullOrWhiteSpace(deal.Uuid))
            return false;

        PendingOpenOrder? pending = null;

        if (deal.RequestId != 0 && _pendingOpens.TryRemove(deal.RequestId, out pending))
        {
            // matched by requestId
        }
        else if (!TryRemoveMatchingOpen(deal.Asset, (decimal)deal.Amount, deal.Command, out pending))
        {
            // unmatched spontaneous open (e.g. external) — still no waiter
            return false;
        }

        if (pending is null)
            return false;

        response = new OrderResponse
        {
            OrderId = deal.Uuid,
            Asset = deal.Asset,
            Direction = pending.Direction,
            Amount = pending.Amount,
            ExpiryTime = pending.PlacedAt.AddSeconds(pending.DurationSeconds),
            PlacedAt = pending.PlacedAt,
            OpenPrice = (decimal)deal.OpenPrice,
            ExpectedPayout = (decimal)deal.Profit,
            Status = OrderStatus.Open,
            BalanceType = deal.IsDemo ? AccountType.Demo : accountType,
            RequestId = pending.RequestId
        };

        return pending.Completion.TrySetResult(response);
    }

    public bool TryCompleteOpenFailure(FailedOrderOpenWire wire)
    {
        PendingOpenOrder? pending = null;

        if (wire.RequestId != 0 && _pendingOpens.TryRemove(wire.RequestId, out pending))
        {
            // matched
        }
        else if (!TryRemoveMatchingOpen(wire.Asset, (decimal)wire.Amount, cmd: null, out pending))
        {
            return false;
        }

        if (pending is null)
            return false;

        var error = string.IsNullOrWhiteSpace(wire.Error) ? "Order open failed." : wire.Error;
        return pending.Completion.TrySetException(new BinollaOrderException(error));
    }

    public TaskCompletionSource<TradeOutcome> RegisterOutcome(string orderId)
    {
        var tcs = new TaskCompletionSource<TradeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_outcomeWaiters.TryAdd(orderId, tcs))
        {
            // already waiting — reuse
            return _outcomeWaiters[orderId];
        }

        return tcs;
    }

    public bool TryCompleteOutcome(string orderId, decimal profitLoss, decimal? closePrice)
    {
        if (!_outcomeWaiters.TryRemove(orderId, out var tcs))
            return false;

        var result = profitLoss > 0
            ? TradeResult.Win
            : profitLoss < 0
                ? TradeResult.Loss
                : TradeResult.Tie;

        var outcome = new TradeOutcome
        {
            OrderId = orderId,
            ProfitLoss = profitLoss,
            ClosePrice = closePrice,
            ClosedAt = DateTimeOffset.UtcNow,
            Result = result
        };

        return tcs.TrySetResult(outcome);
    }

    public void FailAllPending(Exception error)
    {
        foreach (var key in _pendingOpens.Keys.ToArray())
        {
            if (_pendingOpens.TryRemove(key, out var pending))
                pending.Completion.TrySetException(error);
        }

        foreach (var key in _outcomeWaiters.Keys.ToArray())
        {
            if (_outcomeWaiters.TryRemove(key, out var waiter))
                waiter.TrySetException(error);
        }
    }

    public void CancelAllPending()
    {
        foreach (var key in _pendingOpens.Keys.ToArray())
        {
            if (_pendingOpens.TryRemove(key, out var pending))
                pending.Completion.TrySetCanceled();
        }

        foreach (var key in _outcomeWaiters.Keys.ToArray())
        {
            if (_outcomeWaiters.TryRemove(key, out var waiter))
                waiter.TrySetCanceled();
        }
    }

    public bool RemoveOpenWaiter(int requestId) => _pendingOpens.TryRemove(requestId, out _);

    public bool RemoveOutcomeWaiter(string orderId) => _outcomeWaiters.TryRemove(orderId, out _);

    public int PendingOpenCount => _pendingOpens.Count;
    public int PendingOutcomeCount => _outcomeWaiters.Count;

    private bool TryRemoveMatchingOpen(string? asset, decimal amount, int? cmd, out PendingOpenOrder? pending)
    {
        pending = null;
        foreach (var kvp in _pendingOpens)
        {
            var p = kvp.Value;
            if (!string.Equals(p.Asset, asset, StringComparison.OrdinalIgnoreCase))
                continue;

            if (p.Amount != amount)
                continue;

            if (cmd.HasValue && (int)p.Direction != cmd.Value)
                continue;

            if (_pendingOpens.TryRemove(kvp.Key, out pending))
                return true;
        }

        // FIFO fallback: first pending open
        var first = _pendingOpens.OrderBy(x => x.Key).FirstOrDefault();
        if (first.Value is null)
            return false;

        return _pendingOpens.TryRemove(first.Key, out pending);
    }
}
