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
///
/// <para><b>Central decision, parallel fan-out.</b> Bots that run the same strategy for
/// the same trade duration form a <see cref="SignalCohort"/>. Each cohort is scanned
/// ONCE per closed bar — one pass over the union of that cohort's pairs — and the ranked
/// result is handed to every user in the cohort at the same instant, in parallel.</para>
///
/// <para>This replaced a per-user scan loop. There, the market moved between the first
/// user's scan and the last one's: a setup that was live for user #1 had aged past its
/// TTL by the time user #7 was reached, so one account traded and another did not.
/// Scanning is now O(cohorts x pairs) per bar instead of O(users x pairs) per second,
/// which is what makes 100 concurrent users behave identically — and affordably.</para>
///
/// <para>Per-user state is still per-user: open-trade holds, daily limits, stake size and
/// pair selection are applied during fan-out. A user is skipped only for a reason of
/// their own, never because of when their turn came round.</para>
/// </summary>
public sealed class BotSignalWorker : IHostedService
{
    /// <summary>
    /// Must stay 1: Binolla accepts ONE asset/change at a time. Parallel history fetches
    /// thrash the socket so only the last pair gets candles — looks like "bot only analyzes one pair".
    /// </summary>
    private const int ScanParallelism = 1;

    /// <summary>
    /// Cohorts are independent decisions, so they scan concurrently. Kept small because
    /// each one still drives a Binolla socket underneath.
    /// </summary>
    private const int CohortParallelism = 2;

    /// <summary>
    /// How many users are handed the decision at once. This is pure per-user bookkeeping
    /// plus one order placement, so it can be far wider than the scan.
    /// </summary>
    private const int FanOutParallelism = 16;

    /// <summary>
    /// How many users may be tried as the scan's data source before the bar is given up
    /// on. Bounded because each attempt is a full pass over every pair.
    /// </summary>
    private const int MaxScanCandidates = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBotRuntimeService _botRuntime;
    private readonly CohortSignalCache _decisions;
    private readonly ILogger<BotSignalWorker> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BotSignalWorker(
        IServiceScopeFactory scopeFactory,
        IBotRuntimeService botRuntime,
        CohortSignalCache decisions,
        ILogger<BotSignalWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _botRuntime = botRuntime;
        _decisions = decisions;
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

    /// <summary>What a single user's own state says about taking this bar's decision.</summary>
    private sealed record BotReadiness(bool Eligible, bool SkipMarketAccess);

    private async Task TickAsync(CancellationToken ct)
    {
        var running = _botRuntime.ListKnown()
            .Where(b => b.State == BotRunState.Running && b.ResolvedAssets.Count > 0)
            .ToList();
        if (running.Count == 0) return;

        var cohorts = running
            .GroupBy(b => SignalCohort.For(b.StrategyId, b.DurationSeconds))
            .ToList();

        await Parallel.ForEachAsync(
            cohorts,
            new ParallelOptions { MaxDegreeOfParallelism = CohortParallelism, CancellationToken = ct },
            async (group, token) =>
            {
                try
                {
                    await ProcessCohortAsync(group.Key, group.ToList(), token).ConfigureAwait(false);
                }
                catch (ObjectDisposedException ex)
                {
                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                        "H-DISPOSE",
                        "BotSignalWorker.TickAsync",
                        "tick_provider_disposed",
                        new { cohort = group.Key.ToString(), err = ex.ObjectName ?? "unknown" },
                        runId: "missed-entry");
                    // #endregion
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "BotSignalWorker failed for cohort {Cohort}", group.Key);
                }
            }).ConfigureAwait(false);

        var evictAt = DateTimeOffset.UtcNow;
        foreach (var timeframe in cohorts
                     .Select(g => StrategyTimeframes.For(g.Key.StrategyId))
                     .Distinct())
        {
            _decisions.Evict(evictAt, timeframe);
        }
    }

    private async Task ProcessCohortAsync(
        SignalCohort cohort,
        IReadOnlyList<BotRuntimeConfig> bots,
        CancellationToken ct)
    {
        var timeframe = StrategyTimeframes.For(cohort.StrategyId);
        var options = RsiStrategyOptions.FromBotDurationSeconds(cohort.DurationSeconds);

        // This loop ticks every second, but the decision only changes once per bar. When
        // this bar has already been decided with nothing to trade there is nothing left
        // for any user to do, so bail out before touching the database — otherwise 100
        // bots cost 200 trade queries a second for the ~59 seconds the answer cannot change.
        var settled = _decisions.TryGet(cohort, timeframe, DateTimeOffset.UtcNow);
        if (settled is { Candidates.Count: 0 }) return;

        // Establishing readiness first serves two purposes: it heals dropped Binolla
        // sessions, and it tells us which user may act as the scan's data source. Running
        // the scan as a user who already has a trade open would soft-none every pair.
        var readiness = new ConcurrentDictionary<Guid, BotReadiness>();
        await Parallel.ForEachAsync(
            bots,
            new ParallelOptions { MaxDegreeOfParallelism = FanOutParallelism, CancellationToken = ct },
            async (bot, token) =>
            {
                readiness[bot.UserId] = await PrepareBotAsync(bot, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var eligible = bots
            .Where(b => readiness.TryGetValue(b.UserId, out var r) && r.Eligible)
            .ToList();
        if (eligible.Count == 0) return;

        var scanStarted = DateTimeOffset.UtcNow;
        var secIntoMin = SecondsIntoMinute();

        // THE central step: one decision per cohort per bar, however many users are in it.
        var decision = await _decisions.GetOrAddAsync(
            cohort,
            timeframe,
            DateTimeOffset.UtcNow,
            (barTime, token) => ScanCohortAsync(cohort, eligible, options, barTime, token),
            ct).ConfigureAwait(false);

        if (decision is null || decision.Candidates.Count == 0) return;

        var scanMs = (DateTimeOffset.UtcNow - scanStarted).TotalMilliseconds;
        var placed = 0;

        // Fan out WIDE and all at once: every eligible user acts on the same list at the
        // same moment, so nobody's entry ages out waiting for their turn.
        await Parallel.ForEachAsync(
            eligible,
            new ParallelOptions { MaxDegreeOfParallelism = FanOutParallelism, CancellationToken = ct },
            async (bot, token) =>
            {
                try
                {
                    if (await ExecuteForBotAsync(bot, decision, options, token).ConfigureAwait(false))
                        Interlocked.Increment(ref placed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "BotSignalWorker execute failed for user {UserId}", bot.UserId);
                }
            }).ConfigureAwait(false);

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-MISS",
            "BotSignalWorker.ProcessCohortAsync",
            "cohort_tick",
            new
            {
                cohort = cohort.ToString(),
                bots = bots.Count,
                eligible = eligible.Count,
                scanned = decision.AssetsScanned,
                candidates = decision.Candidates.Count,
                bestAsset = decision.Candidates[0].Asset,
                bestSignal = decision.Candidates[0].Signal.Signal,
                placed,
                scanMs = Math.Round(scanMs, 0),
                secIntoMin,
                barTime = decision.ClosedBarTime.ToUnixTimeSeconds(),
                maxLag = options.EffectiveEntryLagSeconds,
                central = true
            },
            runId: "missed-entry");
        // #endregion
    }

    /// <summary>
    /// Heals the user's Binolla session and reports whether their own state allows a
    /// trade on this bar. Never throws — a user who cannot be prepared is simply not
    /// eligible this tick.
    /// </summary>
    private async Task<BotReadiness> PrepareBotAsync(BotRuntimeConfig bot, CancellationToken ct)
    {
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
            if (OpenTradeGate.IsUserHeld(bot.UserId) ||
                await HasBlockingOpenTradeAsync(trades, bot.UserId, ct).ConfigureAwait(false))
            {
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-STUCK1",
                    "BotSignalWorker.PrepareBotAsync",
                    "worker_skip_blocking_open",
                    new { userId = bot.UserId.ToString("N")[..8] },
                    runId: "stuck-running");
                // #endregion
                return new BotReadiness(Eligible: false, SkipMarketAccess: true);
            }

            using (AmbientUserContext.Use(bot.UserId))
            {
                var demo = scope.ServiceProvider.GetRequiredService<IMarketingDemoService>();
                var isDemo = await demo.IsMarketingDemoAsync(bot.UserId, ct).ConfigureAwait(false);
                return new BotReadiness(Eligible: true, SkipMarketAccess: !isDemo);
            }
        }
        catch (ObjectDisposedException ex)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-DISPOSE",
                "BotSignalWorker.PrepareBotAsync",
                "root_provider_disposed",
                new { userId = bot.UserId.ToString("N")[..8], err = ex.ObjectName ?? "unknown" },
                runId: "missed-entry");
            // #endregion
            return new BotReadiness(Eligible: false, SkipMarketAccess: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotSignalWorker prepare failed for user {UserId}", bot.UserId);
            return new BotReadiness(Eligible: false, SkipMarketAccess: true);
        }
    }

    /// <summary>
    /// Scans the cohort's pairs once and ranks every live setup. Market data comes from
    /// <c>MarketAnalysisCache</c>, so which eligible user's session drives the socket
    /// changes nothing about the result — it only needs to be a session that is up.
    /// </summary>
    private async Task<CohortDecision?> ScanCohortAsync(
        SignalCohort cohort,
        IReadOnlyList<BotRuntimeConfig> eligible,
        RsiStrategyOptions options,
        DateTimeOffset barTime,
        CancellationToken ct)
    {
        // Union, not intersection: a pair any member follows is worth analysing, and each
        // user filters the ranked list down to their own selection during fan-out.
        var assets = FxCurrencyAssets.FilterSymbols(
            eligible.SelectMany(b => b.ResolvedAssets)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
        if (assets.Count == 0) return null;

        // Deterministic order so an equal-ranked tie always resolves the same way.
        var ordered = assets.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        var timeframe = StrategyTimeframes.For(cohort.StrategyId);

        // The scan borrows one user's socket. "Connected" is only a proxy for "serving
        // usable market data" — a session can be up and still return nothing — and a
        // cohort must not go blind for a whole bar because the first user in the list is
        // the degraded one. So fall through to the next user when a scan reaches no pair.
        foreach (var scanUser in eligible.Take(MaxScanCandidates))
        {
            var attempt = await ScanAsAsync(cohort, scanUser.UserId, ordered, options, timeframe, barTime, ct)
                .ConfigureAwait(false);
            if (attempt.AssetsScanned > 0) return attempt;

            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-MISS",
                "BotSignalWorker.ScanCohortAsync",
                "scan_source_blind",
                new
                {
                    cohort = cohort.ToString(),
                    userId = scanUser.UserId.ToString("N")[..8],
                    assets = ordered.Count
                },
                runId: "missed-entry");
            // #endregion
        }

        // Every candidate source came back blind — report a failed scan so it is retried
        // rather than cached as "no trades this bar".
        return new CohortDecision(cohort, barTime, Array.Empty<CohortCandidate>(), AssetsScanned: 0);
    }

    /// <summary>Runs one full pass over the cohort's pairs using a single user's session.</summary>
    private async Task<CohortDecision> ScanAsAsync(
        SignalCohort cohort,
        Guid scanUserId,
        IReadOnlyList<string> ordered,
        RsiStrategyOptions options,
        int timeframe,
        DateTimeOffset barTime,
        CancellationToken ct)
    {
        var bag = new ConcurrentBag<CohortCandidate>();
        var scanned = 0;

        // Serial by necessity — Binolla accepts one asset/change at a time.
        await Parallel.ForEachAsync(
            ordered,
            new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism, CancellationToken = ct },
            async (asset, token) =>
            {
                try
                {
                    await using var assetScope = _scopeFactory.CreateAsyncScope();
                    var rsi = assetScope.ServiceProvider.GetRequiredService<RsiSignalAppService>();
                    using (AmbientUserContext.Use(scanUserId))
                    {
                        var signal = await rsi.GetSignalAsync(
                            asset,
                            timeframe,
                            options,
                            autoExecute: false,
                            token,
                            skipMarketAccess: true,
                            strategyId: cohort.StrategyId);

                        if (signal.AutomationError is not ("INSUFFICIENT_HISTORY" or "OPEN_TRADE_EXISTS"))
                            Interlocked.Increment(ref scanned);

                        if (IsLiveSetup(signal, cohort.StrategyId))
                            bag.Add(new CohortCandidate(asset, signal));
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                        "H-DISPOSE",
                        "BotSignalWorker.ScanCohortAsync",
                        "asset_scope_disposed",
                        new { asset, err = ex.ObjectName ?? "unknown" },
                        runId: "missed-entry");
                    // #endregion
                }
                catch
                {
                    // soft-skip pair
                }
            }).ConfigureAwait(false);

        return new CohortDecision(cohort, barTime, Rank(bag.ToList()), scanned);
    }

    /// <summary>
    /// Places this bar's decision for one user: the best-ranked candidate they actually
    /// follow. Returns whether an order went out.
    /// </summary>
    private async Task<bool> ExecuteForBotAsync(
        BotRuntimeConfig bot,
        CohortDecision decision,
        RsiStrategyOptions options,
        CancellationToken ct)
    {
        var userId = bot.UserId;
        if (OpenTradeGate.IsUserHeld(userId) || ct.IsCancellationRequested)
            return false;

        var pick = SelectForBot(bot.ResolvedAssets, decision);
        if (pick is null) return false;

        // Re-checked here rather than at scan time: the decision is shared, but its TTL
        // is wall-clock, and a user reached late must not place a stale entry.
        if (!IsLiveSetup(pick.Signal, bot.StrategyId)) return false;

        try
        {
            await using var execScope = _scopeFactory.CreateAsyncScope();
            var rsi = execScope.ServiceProvider.GetRequiredService<RsiSignalAppService>();
            using (AmbientUserContext.Use(userId))
            {
                OpenTradeGate.MarkUserHeld(userId, bot.DurationSeconds);
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-LIVE",
                    "BotSignalWorker.ExecuteForBotAsync",
                    "best_execute",
                    new
                    {
                        userId = userId.ToString("N")[..8],
                        cohort = decision.Cohort.ToString(),
                        asset = pick.Asset,
                        signal = pick.Signal.Signal,
                        liveRsi = pick.Signal.LiveRsi,
                        closedRsi = pick.Signal.Rsi,
                        successRate = pick.Signal.Backtest?.SuccessRate,
                        candidates = decision.Candidates.Count,
                        secIntoMin = SecondsIntoMinute()
                    },
                    runId: "missed-entry");
                // #endregion

                var executed = await rsi.TryAutoExecuteAsync(pick.Signal, options, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(executed.AutomatedTradeId))
                    return true;

                OpenTradeGate.ReleaseUser(userId);
                return false;
            }
        }
        catch
        {
            OpenTradeGate.ReleaseUser(userId);
            return false;
        }
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

    /// <summary>
    /// The candidate this bot takes from the cohort's shared list: the best-ranked one
    /// the user actually follows.
    ///
    /// <para>This is deliberately a pure function of (user's pairs, shared decision) —
    /// no clock, no session, no per-call state. That is what makes the fan-out
    /// deterministic: ten bots with the same pair list are guaranteed to pick the same
    /// entry, because there is nothing left for them to disagree about.</para>
    /// </summary>
    internal static CohortCandidate? SelectForBot(
        IReadOnlyList<string> selectedAssets,
        CohortDecision decision)
    {
        if (decision.Candidates.Count == 0) return null;

        // An empty selection means the bot follows whatever the cohort scanned.
        if (selectedAssets.Count == 0) return decision.Candidates[0];

        var selected = new HashSet<string>(selectedAssets, StringComparer.OrdinalIgnoreCase);
        return decision.Candidates.FirstOrDefault(c => selected.Contains(c.Asset));
    }

    private static bool IsLiveSetup(StrategySignal signal, string? botStrategyId = null) =>
        StrategyGate.TryValidateForTrade(
            signal,
            DateTimeOffset.UtcNow,
            botStrategyId,
            // The signal already carries its regime; the gate rehydrates from it.
            regime: null,
            out _);

    /// <summary>
    /// Orders live setups best-first. Ranking is deterministic — including the final
    /// tiebreak on symbol — because every user in the cohort walks this one list and they
    /// must all stop at the same entry.
    /// </summary>
    internal static IReadOnlyList<CohortCandidate> Rank(List<CohortCandidate> signals)
    {
        if (signals.Count == 0) return Array.Empty<CohortCandidate>();

        // RSI ranks by zone-backtest strength then by how deep into the zone the bar
        // closed. The EMA strategy has no backtest, so those tiebreakers are 0 and the
        // freshest cross simply wins.
        return signals
            .OrderByDescending(s => s.Signal.Backtest?.SuccessRate ?? 0m)
            .ThenByDescending(s =>
            {
                if (!string.Equals(s.Signal.StrategyId, "rsi", StringComparison.OrdinalIgnoreCase))
                    return 0m;
                var rsi = s.Signal.Rsi;
                if (s.Signal.Signal == "Call")
                    return RsiEntryLevels.CallMax - rsi;
                if (s.Signal.Signal == "Put")
                    return rsi - RsiEntryLevels.PutMin;
                return 0m;
            })
            .ThenBy(s => s.Asset, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
