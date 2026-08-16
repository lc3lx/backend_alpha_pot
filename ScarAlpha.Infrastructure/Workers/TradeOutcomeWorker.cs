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
        Enqueue(tradeId, userId, binollaOrderId, attempt: 0);

    private void Enqueue(Guid tradeId, Guid userId, string binollaOrderId, int attempt) =>
        _queue.Writer.TryWrite(new WorkItem(tradeId, userId, binollaOrderId, attempt));

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
                    // Leave Unknown/Failed as-is; only demote live open trades.
                    if (trade.Status is TradeStatus.Pending or TradeStatus.Running)
                    {
                        await ApplyStatusAsync(trades, trade, TradeStatus.Unknown, null, "RECOVERY_NO_SESSION", ct);
                        _logger.LogWarning(
                            "Recovery: trade {TradeId} user={UserId} order={BinollaOrderId} marked Unknown — no live session and no Binolla order query API",
                            trade.Id, trade.UserId, trade.BinollaOrderId);
                    }
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
            if (orphan is not null && !TradeStateMachine.IsHardTerminal(orphan.Status))
            {
                await ApplyStatusAsync(orphanTrades, orphan, TradeStatus.Unknown, null, "NO_SESSION_FOR_OUTCOME", ct);
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H1",
                    "TradeOutcomeWorker.ProcessAsync",
                    "no_session",
                    new { tradeId = item.TradeId.ToString("N") });
                // #endregion
                _logger.LogWarning(
                    "Outcome: no session for trade {TradeId} — marked Unknown", item.TradeId);
            }
            return;
        }

        int durationSeconds = 60;
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        await using (var prepScope = _scopeFactory.CreateAsyncScope())
        {
            var prepTrades = prepScope.ServiceProvider.GetRequiredService<ITradeRepository>();
            var prepTrade = await prepTrades.GetByIdAsync(item.TradeId, item.UserId, ct);
            if (prepTrade is null) return;
            if (TradeStateMachine.IsHardTerminal(prepTrade.Status))
            {
                _logger.LogInformation(
                    "Outcome ignored (already final) trade={TradeId} status={Status}",
                    prepTrade.Id, prepTrade.Status);
                return;
            }
            durationSeconds = prepTrade.DurationSeconds > 0 ? prepTrade.DurationSeconds : 60;
            createdAt = prepTrade.CreatedAt;
        }

        var waitBudget = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 5, 3600) + 120);
        if (waitBudget < TimeSpan.FromMinutes(15))
            waitBudget = TimeSpan.FromMinutes(15);

        TradeOutcome? outcome = null;
        try
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H1",
                "TradeOutcomeWorker.ProcessAsync",
                "wait_start",
                new
                {
                    tradeId = item.TradeId.ToString("N"),
                    durationSeconds,
                    waitSec = waitBudget.TotalSeconds,
                    attempt = item.Attempt
                });
            // #endregion
            outcome = await client.WaitOutcomeAsync(item.BinollaOrderId, waitBudget, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WaitOutcome failed for trade {TradeId}", item.TradeId);

            // Close may already be buffered — peek cache once.
            if (ex is BinollaTimeoutException)
            {
                try
                {
                    outcome = await client.WaitOutcomeAsync(
                        item.BinollaOrderId, TimeSpan.FromSeconds(3), ct);
                }
                catch
                {
                    outcome = null;
                }
            }

            if (outcome is null)
            {
                await using var failScope = _scopeFactory.CreateAsyncScope();
                var failTrades = failScope.ServiceProvider.GetRequiredService<ITradeRepository>();
                var failTrade = await failTrades.GetByIdAsync(item.TradeId, item.UserId, ct);
                if (failTrade is null || TradeStateMachine.IsHardTerminal(failTrade.Status))
                    return;

                var deadline = createdAt.AddSeconds(durationSeconds + 900);
                var canRetry = DateTimeOffset.UtcNow < deadline && item.Attempt < 4;
                if (ex is BinollaTimeoutException && canRetry)
                {
                    if (failTrade.Status == TradeStatus.Unknown)
                        await ApplyStatusAsync(failTrades, failTrade, TradeStatus.Running, null, null, ct);
                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                        "H1",
                        "TradeOutcomeWorker.ProcessAsync",
                        "wait_retry",
                        new
                        {
                            tradeId = item.TradeId.ToString("N"),
                            attempt = item.Attempt + 1,
                            deadlineUtc = deadline.ToUnixTimeSeconds()
                        });
                    // #endregion
                    Enqueue(item.TradeId, item.UserId, item.BinollaOrderId, item.Attempt + 1);
                    return;
                }

                if (!TradeStateMachine.IsHardTerminal(failTrade.Status))
                {
                    var nextFail = ex is BinollaTimeoutException ? TradeStatus.Unknown : TradeStatus.Failed;
                    var code = ex is BinollaTimeoutException ? "OUTCOME_TIMEOUT" : "BINOLLA_CONNECTION_FAILED";
                    await ApplyStatusAsync(failTrades, failTrade, nextFail, null, code, ct);
                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                        "H1",
                        "TradeOutcomeWorker.ProcessAsync",
                        "wait_failed",
                        new
                        {
                            tradeId = item.TradeId.ToString("N"),
                            err = ex.GetType().Name,
                            next = nextFail.ToString(),
                            durationSeconds
                        });
                    // #endregion
                }
                return;
            }
        }

        await SettleAsync(item, outcome, durationSeconds, sw, ct);
    }

    private async Task SettleAsync(
        WorkItem item,
        TradeOutcome outcome,
        int durationSeconds,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var trades = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var trade = await trades.GetByIdAsync(item.TradeId, item.UserId, ct);
        if (trade is null) return;

        if (TradeStateMachine.IsHardTerminal(trade.Status))
        {
            _logger.LogInformation(
                "Outcome ignored (already final) trade={TradeId} status={Status}",
                trade.Id, trade.Status);
            return;
        }

        var prior = trade.Status;
        var next = outcome.Result switch
        {
            TradeResult.Win => TradeStatus.Profit,
            TradeResult.Loss => TradeStatus.Loss,
            TradeResult.Tie => TradeStatus.Tie,
            _ => TradeStatus.Unknown
        };

        await ApplyStatusAsync(trades, trade, next, outcome.ProfitLoss, null, ct, finalOutcome: true);

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H1+H5",
            "TradeOutcomeWorker.SettleAsync",
            "settled",
            new
            {
                tradeId = trade.Id.ToString("N"),
                result = outcome.Result.ToString(),
                pnl = outcome.ProfitLoss,
                next = next.ToString(),
                prior = prior.ToString(),
                durationSeconds,
                elapsedMs = sw.ElapsedMilliseconds
            });
        // #endregion

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
        CancellationToken ct,
        bool finalOutcome = false)
    {
        var status = trade.Status;
        var applied = finalOutcome && next is TradeStatus.Profit or TradeStatus.Loss or TradeStatus.Tie
            ? TradeStateMachine.TryApplyFinalOutcome(ref status, next)
            : next == TradeStatus.Running && status == TradeStatus.Unknown
                ? ApplyUnknownToRunning(ref status)
                : TradeStateMachine.TryTransition(ref status, next);
        if (!applied)
            return;

        trade.Status = status;
        if (pnl.HasValue) trade.Pnl = pnl;
        if (errorCode is not null) trade.ErrorCode = errorCode;
        else if (next is TradeStatus.Profit or TradeStatus.Loss or TradeStatus.Tie)
            trade.ErrorCode = null;
        trade.UpdatedAt = DateTimeOffset.UtcNow;
        await trades.UpdateAsync(trade, ct);
    }

    private static bool ApplyUnknownToRunning(ref TradeStatus status)
    {
        if (status != TradeStatus.Unknown) return false;
        status = TradeStatus.Running;
        return true;
    }

    private sealed record WorkItem(Guid TradeId, Guid UserId, string BinollaOrderId, int Attempt);

    private sealed class ChannelWorkQueue
    {
        private readonly System.Threading.Channels.Channel<WorkItem> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<WorkItem>();

        public System.Threading.Channels.ChannelWriter<WorkItem> Writer => _channel.Writer;
        public System.Threading.Channels.ChannelReader<WorkItem> Reader => _channel.Reader;
    }
}
