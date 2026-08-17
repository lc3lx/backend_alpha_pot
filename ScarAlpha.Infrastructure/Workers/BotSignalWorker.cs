using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Services;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// Keeps bots analyzing/trading while the Mini App is closed.
/// Picks the best RSI signal across selected pairs and places at most one trade.
/// </summary>
public sealed class BotSignalWorker : IHostedService
{
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
                await Task.Delay(TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

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
        var open = await trades.CountByUserAsync(bot.UserId, TradeStatus.Running, ct: ct);
        var pending = await trades.CountByUserAsync(bot.UserId, TradeStatus.Pending, ct: ct);
        if (open + pending > 0)
            return;

        var rsi = scope.ServiceProvider.GetRequiredService<RsiSignalAppService>();
        using (AmbientUserContext.Use(bot.UserId))
        {
            var options = RsiStrategyOptions.FromBotDurationSeconds(bot.DurationSeconds);
            var signals = new List<(string Asset, StrategySignal Signal)>();
            foreach (var asset in bot.ResolvedAssets)
            {
                try
                {
                    var signal = await rsi.GetSignalAsync(asset, 60, options, autoExecute: false, ct);
                    if (signal.Signal is ("Call" or "Put") &&
                        signal.Backtest is { Passed: true })
                    {
                        signals.Add((asset, signal));
                    }
                }
                catch
                {
                    // soft-skip pair
                }
            }

            var best = PickBest(signals);
            if (best is null) return;

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
                    candidates = signals.Count
                });
            // #endregion

            // Second call still returns Call/Put (anti-repeat only after place).
            await rsi.GetSignalAsync(best.Value.Asset, 60, options, autoExecute: true, ct);
        }
    }

    private static (string Asset, StrategySignal Signal)? PickBest(
        List<(string Asset, StrategySignal Signal)> signals)
    {
        if (signals.Count == 0) return null;

        var oversold = RsiStrategyOptions.Default60Seconds.Oversold;
        var overbought = RsiStrategyOptions.Default60Seconds.Overbought;

        var ordered = signals
            .Where(s => s.Signal.Backtest is { Passed: true })
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
