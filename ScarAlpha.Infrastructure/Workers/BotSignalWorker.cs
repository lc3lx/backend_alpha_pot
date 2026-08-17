using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Services;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// Keeps bots analyzing/trading while the Mini App is closed.
/// Heals Binolla sessions, ignores stale open trades, scans in rotating batches.
/// </summary>
public sealed class BotSignalWorker : IHostedService
{
    private const int ScanParallelism = 8;
    private const int BatchSize = 40;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBotRuntimeService _botRuntime;
    private readonly ILogger<BotSignalWorker> _logger;
    private readonly ConcurrentDictionary<Guid, int> _scanOffsets = new();
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
                var delay = SecondsIntoMinute() <= 25
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromSeconds(4);
                await Task.Delay(delay, ct).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BotSignalWorker failed for user {UserId}", bot.UserId);
            }
        }
    }

    private async Task ProcessBotAsync(BotRuntimeConfig bot, CancellationToken ct)
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
            return;

        var rsi = scope.ServiceProvider.GetRequiredService<RsiSignalAppService>();
        using (AmbientUserContext.Use(bot.UserId))
        {
            var options = RsiStrategyOptions.FromBotDurationSeconds(bot.DurationSeconds);
            var batch = NextBatch(bot);
            var bag = new ConcurrentBag<(string Asset, StrategySignal Signal)>();
            var userId = bot.UserId;
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism, CancellationToken = ct },
                async (asset, token) =>
                {
                    using (AmbientUserContext.Use(userId))
                    {
                        try
                        {
                            var signal = await rsi.GetSignalAsync(asset, 60, options, autoExecute: false, token);
                            if (signal.Signal is ("Call" or "Put") &&
                                signal.Backtest is { Passed: true } &&
                                IsFresh(signal, options))
                            {
                                bag.Add((asset, signal));
                            }
                        }
                        catch
                        {
                            // soft-skip pair
                        }
                    }
                }).ConfigureAwait(false);

            var best = PickBest(bag.ToList(), options);
            if (best is null) return;
            if (!IsFresh(best.Value.Signal, options))
                return;

            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "BOT1",
                "BotSignalWorker.ProcessBotAsync",
                "best_signal",
                new
                {
                    userId = bot.UserId.ToString("N")[..8],
                    asset = best.Value.Asset,
                    signal = best.Value.Signal.Signal,
                    rsi = best.Value.Signal.Rsi,
                    successRate = best.Value.Signal.Backtest?.SuccessRate,
                    candidates = bag.Count,
                    batch = batch.Count
                });
            // #endregion

            await rsi.GetSignalAsync(best.Value.Asset, 60, options, autoExecute: true, ct);
        }
    }

    private IReadOnlyList<string> NextBatch(BotRuntimeConfig bot)
    {
        var assets = bot.ResolvedAssets;
        if (assets.Count <= BatchSize)
            return assets;

        var offset = _scanOffsets.AddOrUpdate(bot.UserId, 0, (_, prev) => prev + BatchSize);
        offset %= assets.Count;
        var list = new List<string>(BatchSize);
        for (var i = 0; i < BatchSize; i++)
            list.Add(assets[(offset + i) % assets.Count]);
        return list;
    }

    private static async Task<bool> HasBlockingOpenTradeAsync(
        ITradeRepository trades,
        Guid userId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var running = await trades.ListByUserAsync(userId, take: 20, status: TradeStatus.Running, ct: ct);
        foreach (var trade in running)
        {
            var duration = trade.DurationSeconds > 0 ? trade.DurationSeconds : 60;
            // Grace after expiry — if still Running past this, do not block new entries.
            if (trade.CreatedAt.AddSeconds(duration + 90) > now)
                return true;
        }

        var pending = await trades.ListByUserAsync(userId, take: 10, status: TradeStatus.Pending, ct: ct);
        foreach (var trade in pending)
        {
            if (trade.CreatedAt.AddMinutes(2) > now)
                return true;
        }

        return false;
    }

    private static bool IsFresh(StrategySignal signal, RsiStrategyOptions options)
    {
        var closedAt = signal.CandleTime.AddSeconds(options.TimeframeSeconds);
        return DateTimeOffset.UtcNow - closedAt <=
               TimeSpan.FromSeconds(Math.Max(1, options.MaxEntryLagSeconds));
    }

    private static (string Asset, StrategySignal Signal)? PickBest(
        List<(string Asset, StrategySignal Signal)> signals,
        RsiStrategyOptions options)
    {
        if (signals.Count == 0) return null;

        var oversold = options.Oversold;
        var overbought = options.Overbought;

        var ordered = signals
            .Where(s => s.Signal.Backtest is { Passed: true } && IsFresh(s.Signal, options))
            .OrderByDescending(s => s.Signal.Backtest!.SuccessRate)
            .ThenByDescending(s =>
            {
                if (s.Signal.Signal == "Call")
                    return oversold - s.Signal.Rsi;
                if (s.Signal.Signal == "Put")
                    return s.Signal.Rsi - overbought;
                return 0m;
            })
            .ToList();

        return ordered.Count == 0 ? null : ordered[0];
    }
}
