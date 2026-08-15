using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Workers;

public sealed class TradeOutcomeWorker : ITradeOutcomeWorker, IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBinollaSessionRestorer _sessionRestorer;
    private readonly ILogger<TradeOutcomeWorker> _logger;
    private readonly ChannelWorkQueue _queue = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TradeOutcomeWorker(
        IServiceScopeFactory scopeFactory,
        IBinollaSessionManager sessions,
        IBinollaSessionRestorer sessionRestorer,
        ILogger<TradeOutcomeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
        _sessionRestorer = sessionRestorer;
        _logger = logger;
    }

    public void Enqueue(Guid tradeId, Guid userId, string binollaOrderId) =>
        _queue.Writer.TryWrite(new WorkItem(tradeId, userId, binollaOrderId));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _ = Task.Run(() => RecoverAfterSessionRestoreAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wait for approved-session restore so open Demo trades can re-attach when possible.
    /// </summary>
    private async Task RecoverAfterSessionRestoreAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await _sessionRestorer.WhenInitialRestoreCompleted.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Open-trade recovery proceeding after session-restore wait timeout");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        await RecoverOpenTradesAsync(ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// After restart: re-enqueue Running trades that still have a live session;
    /// mark irrecoverable open trades as Unknown (Binolla has no order-query API).
    /// </summary>
    public async Task RecoverOpenTradesAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var trades = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            var open = await trades.ListOpenTradesAsync(ct);

            foreach (var trade in open)
            {
                ct.ThrowIfCancellationRequested();

                if (trade.Status == TradeStatus.Pending && string.IsNullOrWhiteSpace(trade.BinollaOrderId))
                {
                    await ApplyStatusAsync(trades, trade, TradeStatus.Failed, null, "RECOVERY_PENDING_NO_ORDER", ct);
                    _logger.LogWarning(
                        "Recovery: Pending trade {TradeId} user={UserId} marked Failed (no Binolla order id)",
                        trade.Id, trade.UserId);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trade.BinollaOrderId))
                {
                    await ApplyStatusAsync(trades, trade, TradeStatus.Unknown, null, "RECOVERY_MISSING_ORDER_ID", ct);
                    continue;
                }

                var client = _sessions.Get(trade.UserId.ToString());
                if (client is not null &&
                    client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
                {
                    _logger.LogInformation(
                        "Recovery: re-enqueue trade {TradeId} order={BinollaOrderId} user={UserId}",
                        trade.Id, trade.BinollaOrderId, trade.UserId);
                    Enqueue(trade.Id, trade.UserId, trade.BinollaOrderId);
                }
                else
                {
                    // Honest limitation: cannot query Binolla for historical order result after process death.
                    await ApplyStatusAsync(trades, trade, TradeStatus.Unknown, null, "RECOVERY_NO_SESSION", ct);
                    _logger.LogWarning(
                        "Recovery: trade {TradeId} user={UserId} order={BinollaOrderId} marked Unknown — no live session and no Binolla order query API",
                        trade.Id, trade.UserId, trade.BinollaOrderId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open-trade recovery failed");
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessAsync(item, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outcome worker failed for trade {TradeId}", item.TradeId);
            }
        }
    }

    private async Task ProcessAsync(WorkItem item, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client = _sessions.Get(item.UserId.ToString());
        if (client is null)
        {
            await using var orphanScope = _scopeFactory.CreateAsyncScope();
            var orphanTrades = orphanScope.ServiceProvider.GetRequiredService<ITradeRepository>();
            var orphan = await orphanTrades.GetByIdAsync(item.TradeId, item.UserId, ct);
            if (orphan is not null && !TradeStateMachine.IsTerminal(orphan.Status))
            {
                await ApplyStatusAsync(orphanTrades, orphan, TradeStatus.Unknown, null, "NO_SESSION_FOR_OUTCOME", ct);
                _logger.LogWarning(
                    "Outcome: no session for trade {TradeId} — marked Unknown", item.TradeId);
            }
            return;
        }

        TradeOutcome outcome;
        try
        {
            outcome = await client.WaitOutcomeAsync(item.BinollaOrderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WaitOutcome failed for trade {TradeId}", item.TradeId);
            await using var failScope = _scopeFactory.CreateAsyncScope();
            var failTrades = failScope.ServiceProvider.GetRequiredService<ITradeRepository>();
            var failTrade = await failTrades.GetByIdAsync(item.TradeId, item.UserId, ct);
            if (failTrade is not null && !TradeStateMachine.IsTerminal(failTrade.Status))
            {
                await ApplyStatusAsync(
                    failTrades, failTrade, TradeStatus.Failed, null, "BINOLLA_CONNECTION_FAILED", ct);
            }
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var trades = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var trade = await trades.GetByIdAsync(item.TradeId, item.UserId, ct);
        if (trade is null) return;

        if (TradeStateMachine.IsTerminal(trade.Status))
        {
            _logger.LogInformation(
                "Outcome ignored (already terminal) trade={TradeId} status={Status}",
                trade.Id, trade.Status);
            return;
        }

        var next = outcome.Result switch
        {
            TradeResult.Win => TradeStatus.Profit,
            TradeResult.Loss => TradeStatus.Loss,
            TradeResult.Tie => TradeStatus.Tie,
            _ => TradeStatus.Unknown
        };

        await ApplyStatusAsync(trades, trade, next, outcome.ProfitLoss, null, ct);

        var notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();
        var (variant, title, description) = next switch
        {
            TradeStatus.Profit => ("trade-profit", "Trade profit", $"{trade.Asset} closed in profit."),
            TradeStatus.Loss => ("trade-loss", "Trade loss", $"{trade.Asset} closed in loss."),
            TradeStatus.Tie => ("live-trade", "Trade tied", $"{trade.Asset} closed as a tie."),
            TradeStatus.Failed => ("trade-loss", "Trade failed", $"{trade.Asset} could not be completed."),
            _ => ("live-trade", "Trade updated", $"{trade.Asset} status: {next}.")
        };
        await notifications.AddAsync(
            trade.UserId,
            variant,
            title,
            description,
            trade.Id,
            $"/trading/{trade.Id}",
            ct);

        _logger.LogInformation(
            "Trade outcome tradeId={TradeId} binollaOrderId={BinollaOrderId} status={Status} pnl={Pnl} elapsedMs={ElapsedMs}",
            trade.Id, item.BinollaOrderId, next, outcome.ProfitLoss, sw.ElapsedMilliseconds);
    }

    private static async Task ApplyStatusAsync(
        ITradeRepository trades,
        Domain.Entities.Trade trade,
        TradeStatus next,
        decimal? pnl,
        string? errorCode,
        CancellationToken ct)
    {
        var status = trade.Status;
        if (!TradeStateMachine.TryTransition(ref status, next))
            return;

        trade.Status = status;
        if (pnl.HasValue) trade.Pnl = pnl;
        if (errorCode is not null) trade.ErrorCode = errorCode;
        trade.UpdatedAt = DateTimeOffset.UtcNow;
        await trades.UpdateAsync(trade, ct);
    }

    private sealed record WorkItem(Guid TradeId, Guid UserId, string BinollaOrderId);

    private sealed class ChannelWorkQueue
    {
        private readonly System.Threading.Channels.Channel<WorkItem> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<WorkItem>();

        public System.Threading.Channels.ChannelWriter<WorkItem> Writer => _channel.Writer;
        public System.Threading.Channels.ChannelReader<WorkItem> Reader => _channel.Reader;
    }
}
