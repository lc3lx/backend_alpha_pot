using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// Keeps every pair the bots watch subscribed and cached, continuously.
///
/// <para><b>Why entries were late.</b> History used to be fetched on demand, inside the
/// bar-close scan. At each close the cached series was stale, so every pair evicted its
/// history, re-sent <c>asset/change</c>, and blocked waiting for Binolla to push it back.
/// Binolla accepts one <c>asset/change</c> at a time, so that cost ran SERIALLY across
/// the whole pair list — roughly 0.5-1s each, which is exactly the ~15s gap seen between
/// a bar closing and the order going out.</para>
///
/// <para><b>Why warming removes it.</b> Binolla's <c>s_quotes/list</c> carries many pairs
/// per frame, and the router applies each one to that pair's minute bars. So once a
/// pair's history is resident it advances by itself, tick by tick, and never goes stale.
/// The scan at bar close then reads RAM and the entry is placed within milliseconds —
/// for every user at once, because they all read the same warm cache.</para>
///
/// <para>This worker exists to establish and hold that residency. It runs off the entry
/// path entirely, so a cold or slow pair delays nothing.</para>
/// </summary>
public sealed class MarketWarmupWorker : IHostedService
{
    /// <summary>
    /// How often the working set is reconciled. Short enough to recover a dropped pair
    /// well inside one bar, long enough that a warm set costs almost nothing — the warm
    /// call returns immediately when history and quote are both fresh.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gap between subscribes for pairs that are actually cold. Binolla serialises
    /// <c>asset/change</c>, and firing a burst makes it drop in-flight history pushes —
    /// the original reason the scan had to be serial in the first place.
    /// </summary>
    private static readonly TimeSpan SubscribeSpacing = TimeSpan.FromMilliseconds(250);

    /// <summary>Cold pairs warmed per sweep, so one bad pass cannot stall the loop.</summary>
    private const int MaxWarmPerSweep = 12;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBotRuntimeService _botRuntime;
    private readonly ILogger<MarketWarmupWorker> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MarketWarmupWorker(
        IServiceScopeFactory scopeFactory,
        IBotRuntimeService botRuntime,
        ILogger<MarketWarmupWorker> logger)
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
                await SweepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MarketWarmupWorker sweep failed");
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
        var running = _botRuntime.ListKnown()
            .Where(b => b.State == BotRunState.Running && b.ResolvedAssets.Count > 0)
            .ToList();
        if (running.Count == 0) return;

        // (pair, timeframe) pairs the bots will actually ask for at the next bar close.
        var wanted = running
            .SelectMany(b => FxCurrencyAssets
                .FilterSymbols(b.ResolvedAssets)
                .Select(a => (Asset: a, Timeframe: StrategyTimeframes.For(b.StrategyId))))
            .Distinct()
            .OrderBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IBinollaSessionManager>();

        // Warming is per session: each user's socket has its own cache, and a pair is
        // only free at bar close for the sessions that were actually holding it.
        var userIds = running.Select(b => b.UserId).Distinct().ToList();
        var warmedCount = 0;
        var alreadyHot = 0;

        foreach (var userId in userIds)
        {
            if (ct.IsCancellationRequested) return;

            var client = sessions.Get(userId.ToString());
            if (client is null ||
                !client.IsTransportConnected ||
                client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
            {
                continue;
            }

            foreach (var (asset, timeframe) in wanted)
            {
                if (ct.IsCancellationRequested) return;

                if (client.HasFreshHistory(asset, timeframe))
                {
                    alreadyHot++;
                    continue;
                }

                if (warmedCount >= MaxWarmPerSweep) break;

                try
                {
                    // Fire-and-forget inside the client: it subscribes and waits on its
                    // own task, so a cold pair never blocks this loop or the entry path.
                    client.EnsureMarketDataWarm(asset, timeframe);
                    warmedCount++;
                }
                catch
                {
                    // soft — next sweep retries
                }

                await Task.Delay(SubscribeSpacing, ct).ConfigureAwait(false);
            }
        }

        if (warmedCount > 0)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-WARM",
                "MarketWarmupWorker.SweepAsync",
                "warm_sweep",
                new
                {
                    sessions = userIds.Count,
                    wanted = wanted.Count,
                    warmed = warmedCount,
                    alreadyHot
                },
                runId: "missed-entry");
            // #endregion
        }
    }
}
