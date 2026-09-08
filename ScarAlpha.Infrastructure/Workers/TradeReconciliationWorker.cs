using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Persistence;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// Checks settled trades against Binolla's own record and corrects the ones that disagree.
///
/// <para><b>Why this is needed.</b> A trade's outcome is decided from the first closed-deal
/// frame that arrives for its order id, and that decision is final — Profit/Loss/Tie are
/// hard-terminal states. When that first frame is wrong or belongs to a different stage of
/// the order's life, the trade keeps the wrong result forever: users saw trades that had
/// won on Binolla recorded as losses, with the balance and the history permanently
/// disagreeing with their broker account.</para>
///
/// <para>Correctness here cannot rest on getting the live frame right every time, because
/// the live frame is the thing that is occasionally wrong. It rests on asking the broker
/// again afterwards, when the order is unambiguously closed, and believing that answer.</para>
///
/// <para>The broker is the authority. When it disagrees, the trade is corrected, the
/// change is written to the audit log, and the user is notified — a silent correction of
/// a number someone may have already acted on would be worse than the original error.</para>
/// </summary>
public sealed class TradeReconciliationWorker : IHostedService
{
    /// <summary>How often to sweep for unverified trades.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Leave a trade alone until the broker has certainly published its close. Checking
    /// too early reads the same in-flight state the live path already misread.
    /// </summary>
    private static readonly TimeSpan SettleGrace = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Stop chasing a trade this old. Binolla's session cache does not keep closed orders
    /// forever, so past this point "not found" tells us nothing and we would re-query for ever.
    /// </summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>Trades examined per sweep, so one pass cannot monopolise the database.</summary>
    private const int BatchSize = 60;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBinollaSessionManager _sessions;
    private readonly ILogger<TradeReconciliationWorker> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TradeReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        IBinollaSessionManager sessions,
        ILogger<TradeReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); } catch { /* ignore */ }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TradeReconciliationWorker sweep failed");
            }

            try
            {
                await Task.Delay(SweepInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var settledBefore = now - SettleGrace;
        var oldestToCheck = now - MaxAge;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidates = await db.Trades
            .Where(t => t.VerifiedAt == null
                        && t.BinollaOrderId != null
                        && t.UpdatedAt <= settledBefore
                        && t.CreatedAt >= oldestToCheck
                        && (t.Status == TradeStatus.Profit
                            || t.Status == TradeStatus.Loss
                            || t.Status == TradeStatus.Tie))
            .OrderBy(t => t.UpdatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var corrected = 0;
        var confirmed = 0;

        foreach (var trade in candidates)
        {
            if (ct.IsCancellationRequested) return;

            var client = _sessions.Get(trade.UserId.ToString());
            if (client is null || !client.TryGetClosedPnl(trade.BinollaOrderId!, out var brokerPnl))
            {
                // No session, or the broker has no record in this session's cache. Say
                // nothing rather than guessing — an unverifiable trade keeps its value and
                // ages out of the window on its own.
                continue;
            }

            var brokerStatus = brokerPnl > 0m
                ? TradeStatus.Profit
                : brokerPnl < 0m ? TradeStatus.Loss : TradeStatus.Tie;

            trade.VerifiedAt = now;

            if (brokerStatus == trade.Status && trade.Pnl == brokerPnl)
            {
                confirmed++;
                continue;
            }

            var priorStatus = trade.Status;
            var priorPnl = trade.Pnl;
            var status = trade.Status;

            if (!TradeStateMachine.TryApplyBrokerCorrection(ref status, brokerStatus))
            {
                // Same status, different amount: still worth storing the broker's figure.
                trade.Pnl = brokerPnl;
                trade.UpdatedAt = now;
                confirmed++;
                continue;
            }

            trade.Status = status;
            trade.Pnl = brokerPnl;
            trade.UpdatedAt = now;
            corrected++;

            _logger.LogWarning(
                "Trade corrected from Binolla: trade={TradeId} user={UserId} {Prior}({PriorPnl}) → {Next}({Pnl})",
                trade.Id, trade.UserId, priorStatus, priorPnl, status, brokerPnl);

            await NotifyCorrectionAsync(scope, trade, priorStatus, ct).ConfigureAwait(false);
            await AuditCorrectionAsync(scope, trade, priorStatus, priorPnl, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct);

        if (corrected > 0 || confirmed > 0)
        {
            _logger.LogInformation(
                "Reconciliation sweep: {Checked} checked, {Confirmed} confirmed, {Corrected} corrected",
                candidates.Count, confirmed, corrected);
        }
    }

    private static async Task NotifyCorrectionAsync(
        IServiceScope scope,
        Domain.Entities.Trade trade,
        TradeStatus priorStatus,
        CancellationToken ct)
    {
        try
        {
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();
            var variant = trade.Status == TradeStatus.Loss ? "trade-loss" : "trade-profit";
            await notifications.AddAsync(
                trade.UserId,
                variant,
                "Trade result corrected",
                $"{trade.Asset} was recorded as {priorStatus} and has been corrected to "
                + $"{trade.Status} to match Binolla.",
                trade.Id,
                $"/trading/{trade.Id}",
                ct);
        }
        catch
        {
            // A failed notification must not roll back the correction itself.
        }
    }

    private static async Task AuditCorrectionAsync(
        IServiceScope scope,
        Domain.Entities.Trade trade,
        TradeStatus priorStatus,
        decimal? priorPnl,
        CancellationToken ct)
    {
        try
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await audit.RecordAsync(
                action: "TradeOutcomeCorrected",
                // No human actor: the broker's own record drove this change.
                actorUserId: Guid.Empty,
                targetUserId: trade.UserId,
                targetBinollaLinkId: null,
                previousState: $"{priorStatus}:{priorPnl}",
                newState: $"{trade.Status}:{trade.Pnl}",
                detail: $"tradeId={trade.Id};orderId={trade.BinollaOrderId};source=binolla",
                ct: ct);
        }
        catch
        {
            // Same reasoning as the notification.
        }
    }
}
