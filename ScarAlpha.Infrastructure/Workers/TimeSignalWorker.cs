using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Application.Services;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Persistence;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// Background worker that monitors scheduled time signals and automatically executes them
/// across all active users running the "time_analysis" strategy when the scheduled time arrives.
/// </summary>
public sealed class TimeSignalWorker : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBotRuntimeService _botRuntime;
    private readonly IBotMaintenanceService _maintenance;
    private readonly IBrokerResolver _brokerResolver;
    private readonly ILogger<TimeSignalWorker> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TimeSignalWorker(
        IServiceScopeFactory scopeFactory,
        IBotRuntimeService botRuntime,
        IBotMaintenanceService maintenance,
        IBrokerResolver brokerResolver,
        ILogger<TimeSignalWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _botRuntime = botRuntime;
        _maintenance = maintenance;
        _brokerResolver = brokerResolver;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("TimeSignalWorker started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); } catch { /* ignore */ }
        }
        _logger.LogInformation("TimeSignalWorker stopped");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TimeSignalWorker tick failed");
            }

            try
            {
                // Align sleep to the exact start of the next second
                var ms = DateTimeOffset.UtcNow.Millisecond;
                var delay = Math.Max(100, 1000 - ms);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_maintenance.Current.Active) return;

        var now = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Query pending signals due up to current second
        var pendingSignals = await db.TimeSignals
            .Where(s => s.Status == TimeSignalStatus.Pending && s.ScheduledTime <= now)
            .OrderBy(s => s.ScheduledTime)
            .Take(10)
            .ToListAsync(ct);

        if (pendingSignals.Count == 0) return;

        foreach (var signal in pendingSignals)
        {
            if (ct.IsCancellationRequested) break;

            // If older than 2 minutes: expire to avoid placing dangerous late trades
            if (signal.ScheduledTime < now.AddMinutes(-2))
            {
                signal.Status = TimeSignalStatus.Expired;
                signal.ErrorMessage = "EXPIRED_STALE_SIGNAL: execution time passed by more than 2 minutes";
                signal.ExecutedAt = now;
                await db.SaveChangesAsync(ct);
                _logger.LogWarning("Time signal {Id} on {Asset} expired (scheduled at {Time})",
                    signal.Id, signal.Asset, signal.ScheduledTime);
                continue;
            }

            // Mark executing atomically
            signal.Status = TimeSignalStatus.Executing;
            await db.SaveChangesAsync(ct);

            await ExecuteSignalAsync(signal, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteSignalAsync(TimeSignal signal, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        _logger.LogInformation("Executing time signal {Id}: {Asset} {Direction} scheduled={Time}",
            signal.Id, signal.Asset, signal.Direction, signal.ScheduledTime);

        // Find users running time_analysis
        var targetBots = _botRuntime.ListKnown()
            .Where(b => b.State == BotRunState.Running && IsTimeAnalysis(b.StrategyId))
            .ToList();

        if (targetBots.Count == 0)
        {
            await using var finishScope = _scopeFactory.CreateAsyncScope();
            var db = finishScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var s = await db.TimeSignals.FindAsync(new object[] { signal.Id }, ct);
            if (s is not null)
            {
                s.Status = TimeSignalStatus.Executed;
                s.TargetCount = 0;
                s.ExecutedCount = 0;
                s.FailedCount = 0;
                s.ExecutedAt = now;
                s.ExecutionLog = "No active users subscribed to time_analysis strategy.";
                await db.SaveChangesAsync(ct);
            }
            _logger.LogInformation("Time signal {Id} finished: 0 active users", signal.Id);
            return;
        }

        var successCount = 0;
        var failCount = 0;
        var logBag = new ConcurrentBag<string>();

        // Fan out parallel execution across all users
        await Parallel.ForEachAsync(
            targetBots,
            new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct },
            async (bot, token) =>
            {
                var userId = bot.UserId;
                try
                {
                    await using var userScope = _scopeFactory.CreateAsyncScope();
                    var sessions = userScope.ServiceProvider.GetRequiredService<IBrokerSessionManager>();
                    var restorer = userScope.ServiceProvider.GetRequiredService<IBinollaSessionRestorer>();
                    var tradeAppService = userScope.ServiceProvider.GetRequiredService<TradeAppService>();
                    var rsiAppService = userScope.ServiceProvider.GetRequiredService<RsiSignalAppService>();

                    var broker = await _brokerResolver.GetAsync(userId, token).ConfigureAwait(false);

                    // Auto-heal session if needed
                    var client = sessions.Get(userId, broker);
                    if (client is null ||
                        !client.IsTransportConnected ||
                        client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
                    {
                        restorer.EnsureBackgroundRestore(userId);
                    }

                    // Check daily limits
                    using (AmbientUserContext.Use(userId))
                    {
                        var dailyLimit = await rsiAppService.GetReachedDailyLimitAsync(bot, token).ConfigureAwait(false);
                        if (dailyLimit is not null)
                        {
                            _botRuntime.Stop(userId, dailyLimit);
                            Interlocked.Increment(ref failCount);
                            logBag.Add($"[LIMIT] User {userId.ToString("N")[..8]}: {dailyLimit}");
                            return;
                        }

                        // Check open trade gate
                        if (OpenTradeGate.IsUserHeld(userId))
                        {
                            Interlocked.Increment(ref failCount);
                            logBag.Add($"[BUSY] User {userId.ToString("N")[..8]}: open trade active");
                            return;
                        }

                        var duration = signal.DurationSeconds > 0 ? signal.DurationSeconds : bot.DurationSeconds;
                        if (duration is < 5 or > 3600) duration = 60;
                        var amount = bot.EffectiveBaseAmount > 0 ? bot.EffectiveBaseAmount : bot.Amount;
                        if (amount <= 0) amount = 25m;

                        var key = $"time-signal:{signal.Id}:{userId}";
                        var req = new PlaceTradeRequest(
                            Asset: signal.Asset,
                            Direction: signal.Direction,
                            Amount: amount,
                            DurationSeconds: duration,
                            StrategyId: "time_analysis"
                        );

                        OpenTradeGate.MarkUserHeld(userId, duration);
                        try
                        {
                            var placed = await tradeAppService.PlaceTradeAsync(req, key, token).ConfigureAwait(false);
                            Interlocked.Increment(ref successCount);
                            logBag.Add($"[OK] User {userId.ToString("N")[..8]}: Trade {placed.Id[..8]} placed ${amount}");
                        }
                        catch (Exception ex)
                        {
                            OpenTradeGate.ReleaseUser(userId);
                            Interlocked.Increment(ref failCount);
                            logBag.Add($"[ERROR] User {userId.ToString("N")[..8]}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failCount);
                    logBag.Add($"[FATAL] User {userId.ToString("N")[..8]}: {ex.Message}");
                }
            }).ConfigureAwait(false);

        // Update signal record with execution summary
        await using var updateScope = _scopeFactory.CreateAsyncScope();
        var updateDb = updateScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedSignal = await updateDb.TimeSignals.FindAsync(new object[] { signal.Id }, ct);
        if (persistedSignal is not null)
        {
            persistedSignal.TargetCount = targetBots.Count;
            persistedSignal.ExecutedCount = successCount;
            persistedSignal.FailedCount = failCount;
            persistedSignal.ExecutedAt = DateTimeOffset.UtcNow;
            persistedSignal.Status = successCount > 0 ? TimeSignalStatus.Executed : (targetBots.Count > 0 ? TimeSignalStatus.Failed : TimeSignalStatus.Executed);
            persistedSignal.ExecutionLog = string.Join("\n", logBag.Take(50));
            await updateDb.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Time signal {Id} executed: {Success}/{Total} succeeded, {Failed} failed",
            signal.Id, successCount, targetBots.Count, failCount);
    }

    private static bool IsTimeAnalysis(string? strategyId) =>
        string.Equals(strategyId?.Trim(), "time_analysis", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(strategyId?.Trim(), "temporal", StringComparison.OrdinalIgnoreCase);
}
