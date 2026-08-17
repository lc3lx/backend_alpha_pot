using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Services;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// Keeps bots analyzing/trading while the Mini App is closed.
/// Scans every selected pair each tick (RSI + 200-candle zone backtest in parallel).
/// </summary>
public sealed class BotSignalWorker : IHostedService
{
    private const int ScanParallelism = 8;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBotRuntimeService _botRuntime;
    private readonly ILogger<BotSignalWorker> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BotSignalWorker(
        IServiceScopeFactory scopeFactory,
        IBotRuntimeService botRuntime,
        ILogger<BotSignalWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _botRuntime = botRuntime;
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
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BotSignalWorker tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static int SecondsIntoMinute() => DateTimeOffset.UtcNow.Second;

    private async Task TickAsync(CancellationToken ct)
    {
        var running = _botRuntime.ListKnown()
            .Where(b => b.State == BotRunState.Running && b.ResolvedAssets.Count > 0)
            .ToList();
        if (running.Count == 0) return;

        foreach (var bot in running)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessBotAsync(bot, ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-DISPOSE",
                    "BotSignalWorker.TickAsync",
                    "tick_provider_disposed",
                    new { userId = bot.UserId.ToString("N")[..8], err = ex.ObjectName ?? "unknown" },
                    runId: "missed-entry");
                // #endregion
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BotSignalWorker failed for user {UserId}", bot.UserId);
            }
        }
    }

    private async Task ProcessBotAsync(BotRuntimeConfig bot, CancellationToken ct)
    {
        var skipMarketAccess = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var trades = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            var sessions = scope.ServiceProvider.GetRequiredService<IBinollaSessionManager>();
            var restorer = scope.ServiceProvider.GetRequiredService<IBinollaSessionRestorer>();

            // Auto-heal Binolla session so Running bots keep trading without manual Start.
            var client = sessions.Get(bot.UserId.ToString());
            if (client is null ||
                !client.IsTransportConnected ||
                client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
            {
                restorer.EnsureBackgroundRestore(bot.UserId);
                try
                {
                    using (AmbientUserContext.Use(bot.UserId))
                    {
                        var binolla = scope.ServiceProvider.GetRequiredService<BinollaAppService>();
                        await binolla.TryReloginFromStoredCredentialsAsync(ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // soft — next tick retries
                }
            }

            // Only block on truly live open trades — expired/stuck ones must not freeze the bot.
            if (await HasBlockingOpenTradeAsync(trades, bot.UserId, ct).ConfigureAwait(false))
            {
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-STUCK1",
                    "BotSignalWorker.ProcessBotAsync",
                    "worker_skip_blocking_open",
                    new { userId = bot.UserId.ToString("N")[..8] },
                    runId: "stuck-running");
                // #endregion
                return;
            }

            using (AmbientUserContext.Use(bot.UserId))
            {
                var demo = scope.ServiceProvider.GetRequiredService<IMarketingDemoService>();
                skipMarketAccess = !await demo.IsMarketingDemoAsync(bot.UserId, ct).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException ex)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-DISPOSE",
                "BotSignalWorker.ProcessBotAsync",
                "root_provider_disposed",
                new { userId = bot.UserId.ToString("N")[..8], err = ex.ObjectName ?? "unknown" },
                runId: "missed-entry");
            // #endregion
            return;
        }

        var options = RsiStrategyOptions.FromBotDurationSeconds(bot.DurationSeconds);
        var batch = bot.ResolvedAssets;
        var bag = new ConcurrentBag<(string Asset, StrategySignal Signal)>();
        var staleHits = 0;
        var softNone = 0;
        var dbRace = 0;
        var placedCount = 0;
        var claimed = 0;
        var userId = bot.UserId;
        var scanStarted = DateTimeOffset.UtcNow;
        var secIntoMin = SecondsIntoMinute();

        // Analyze in parallel (no place). First live Call/Put executes immediately
        // on that pair's own scope so we do not wait for the rest of the universe.
        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism, CancellationToken = ct },
            async (asset, token) =>
            {
                if (Volatile.Read(ref claimed) != 0)
                    return;

                try
                {
                    await using var assetScope = _scopeFactory.CreateAsyncScope();
                    var rsi = assetScope.ServiceProvider.GetRequiredService<RsiSignalAppService>();
                    using (AmbientUserContext.Use(userId))
                    {
                        var signal = await rsi.GetSignalAsync(
                            asset, 60, options, autoExecute: false, token, skipMarketAccess);
                        if (signal.AutomationError == "SIGNAL_STALE")
                            Interlocked.Increment(ref staleHits);
                        else if (signal.Signal is not ("Call" or "Put"))
                            Interlocked.Increment(ref softNone);

                        if (!IsLiveSetup(signal))
                            return;

                        bag.Add((asset, signal));
                        if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
                            return;

                        // #region agent log
                        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                            "H-LIVE",
                            "BotSignalWorker.ProcessBotAsync",
                            "immediate_execute",
                            new
                            {
                                asset,
                                signal = signal.Signal,
                                liveRsi = signal.LiveRsi,
                                closedRsi = signal.Rsi,
                                successRate = signal.Backtest?.SuccessRate,
                                secIntoMin
                            },
                            runId: "missed-entry");
                        // #endregion

                        var executed = await rsi.TryAutoExecuteAsync(signal, options, token);
                        if (!string.IsNullOrEmpty(executed.AutomatedTradeId))
                            Interlocked.Increment(ref placedCount);
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                        "H-DISPOSE",
                        "BotSignalWorker.ProcessBotAsync",
                        "asset_scope_disposed",
                        new { asset, err = ex.ObjectName ?? "unknown" },
                        runId: "missed-entry");
                    // #endregion
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("second operation", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("DbContext", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref dbRace);
                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                        "H-DB1",
                        "BotSignalWorker.ProcessBotAsync",
                        "dbcontext_race_caught",
                        new { asset, err = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message });
                    // #endregion
                }
                catch
                {
                    // soft-skip pair
                }
            }).ConfigureAwait(false);

        var scanMs = (DateTimeOffset.UtcNow - scanStarted).TotalMilliseconds;
        var best = PickBest(bag.ToList());

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-MISS",
            "BotSignalWorker.ProcessBotAsync",
            "scan_tick",
            new
            {
                userId = bot.UserId.ToString("N")[..8],
                assetCount = bot.ResolvedAssets.Count,
                batch = batch.Count,
                scanMs = Math.Round(scanMs, 0),
                secIntoMin,
                candidates = bag.Count,
                staleHits,
                softNone,
                dbRace,
                placedCount,
                claimed,
                maxLag = options.MaxEntryLagSeconds,
                lookback = options.BacktestCandleCount,
                hasBest = best is not null,
                skipMarketAccess,
                scopedPerAsset = true
            },
            runId: "missed-entry");
        // #endregion
    }

    private static async Task<bool> HasBlockingOpenTradeAsync(
        ITradeRepository trades,
        Guid userId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var running = await trades.ListByUserAsync(userId, take: 20, status: TradeStatus.Running, ct: ct);
        var pending = await trades.ListByUserAsync(userId, take: 10, status: TradeStatus.Pending, ct: ct);
        var blockers = running.Concat(pending).Where(trade => OpenTradeGate.IsBlocking(trade, now)).ToList();
        var staleRunning = running.Count(trade => !OpenTradeGate.IsBlocking(trade, now));
        if (blockers.Count > 0 || staleRunning > 0)
        {
            var first = blockers.OrderBy(t => t.CreatedAt).FirstOrDefault();
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-STUCK1",
                "BotSignalWorker.HasBlockingOpenTradeAsync",
                blockers.Count > 0 ? "has_blocking_open" : "stale_open_ignored",
                new
                {
                    userId = userId.ToString("N")[..8],
                    blocking = blockers.Count,
                    staleRunning,
                    pending = pending.Count,
                    asset = first?.Asset,
                    direction = first?.Direction.ToString(),
                    amount = first?.Amount,
                    durationSec = first?.DurationSeconds,
                    status = first?.Status.ToString(),
                    ageSec = first is null ? 0 : Math.Round((now - first.CreatedAt).TotalSeconds, 0),
                    tradeId = first is null ? null : first.Id.ToString("N")[..8]
                },
                runId: "stuck-running");
            // #endregion
        }

        return blockers.Count > 0;
    }

    private static bool IsLiveSetup(StrategySignal signal) =>
        RsiEntryLevels.TryValidateForTrade(signal, DateTimeOffset.UtcNow, out _);

    private static (string Asset, StrategySignal Signal)? PickBest(
        List<(string Asset, StrategySignal Signal)> signals)
    {
        if (signals.Count == 0) return null;

        var ordered = signals
            .Where(s => IsLiveSetup(s.Signal))
            .OrderByDescending(s => s.Signal.Backtest!.SuccessRate)
            .ThenByDescending(s =>
            {
                var rsi = s.Signal.LiveRsi ?? s.Signal.Rsi;
                if (s.Signal.Signal == "Call")
                    return RsiEntryLevels.CallMax - rsi;
                if (s.Signal.Signal == "Put")
                    return rsi - RsiEntryLevels.PutMin;
                return 0m;
            })
            .ToList();

        return ordered.Count == 0 ? null : ordered[0];
    }
}
