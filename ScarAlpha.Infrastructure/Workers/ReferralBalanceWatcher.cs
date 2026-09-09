using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// Backstop for the referral deposit signal. Real-balance readings normally arrive for
/// free from <c>TradeAppService.PlaceTradeAsync</c>'s own balance check, but a user who
/// deposits and never places a trade would otherwise stay undetected forever. This worker
/// periodically re-checks live Binolla sessions for any not-yet-qualified referred user —
/// never forcing a reconnect, only reading balance where a session is already live.
/// </summary>
public sealed class ReferralBalanceWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBrokerSessionManager _sessions;
    private readonly IBrokerResolver _brokers;
    private readonly ILogger<ReferralBalanceWatcher> _logger;

    public ReferralBalanceWatcher(
        IServiceScopeFactory scopeFactory,
        IBrokerSessionManager sessions,
        IBrokerResolver brokers,
        ILogger<ReferralBalanceWatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
        _brokers = brokers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let session restore finish before the first pass so early polls aren't wasted.
        if (!await DelayAsync(TimeSpan.FromMinutes(1), stoppingToken))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Referral balance watch pass failed");
            }

            if (!await DelayAsync(TimeSpan.FromMinutes(Math.Max(1, ReferralConfig.BalancePollMinutes)), stoppingToken))
                return;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var referrals = scope.ServiceProvider.GetRequiredService<IReferralRepository>();
        var qualification = scope.ServiceProvider.GetRequiredService<IReferralQualificationService>();

        var pending = await referrals.ListDepositWatchAsync(500, ct);
        foreach (var q in pending)
        {
            ct.ThrowIfCancellationRequested();

            var broker = await _brokers.GetAsync(q.ReferredUserId, ct).ConfigureAwait(false);
            var client = _sessions.Get(q.ReferredUserId, broker);
            if (client is null ||
                client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
                continue;

            try
            {
                var balance = await client.GetBalanceAsync(ct);
                await qualification.ObserveRealBalanceAsync(q.ReferredUserId, balance.RealBalance, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Referral balance poll failed for user {UserId}", q.ReferredUserId);
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
