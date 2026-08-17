using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

public sealed class RsiSignalAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBotAccessService _botAccess;
    private readonly IRsiSignalService _signalService;
    private readonly IBinollaSessionRestorer _restorer;
    private readonly IMarketingDemoService _demo;
    private readonly TradeAppService _trades;
    private readonly IBotRuntimeService _botRuntime;
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<RsiSignalAppService> _logger;

    public RsiSignalAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        IRsiSignalService signalService,
        IBinollaSessionRestorer restorer,
        IMarketingDemoService demo,
        TradeAppService trades,
        IBotRuntimeService botRuntime,
        ITradeRepository tradeRepository,
        ILogger<RsiSignalAppService> logger)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _botAccess = botAccess;
        _signalService = signalService;
        _restorer = restorer;
        _demo = demo;
        _trades = trades;
        _botRuntime = botRuntime;
        _tradeRepository = tradeRepository;
        _logger = logger;
    }

    public Task<StrategySignal> TryAutoExecuteAsync(
        StrategySignal signal,
        RsiStrategyOptions options,
        CancellationToken ct) =>
        ExecuteIfRequestedAsync(signal, options, autoExecute: true, ct);

    public async Task<StrategySignal> GetSignalAsync(
        string asset,
        int periodSeconds,
        RsiStrategyOptions options,
        bool autoExecute,
        CancellationToken ct,
        bool skipMarketAccess = false)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (periodSeconds != 60)
            throw new ApiException(ApiErrorCodes.ValidationError, "RSI Smart Backtest only supports the 1-minute timeframe.");

        if (!skipMarketAccess && await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
        {
            // Demo payloads are display-only — never auto-place from synthetic RSI.
            return _demo.BuildRsiSignal(asset, periodSeconds);
        }

        if (!skipMarketAccess)
        {
            var access = await _botAccess.CheckAsync(_currentUser.UserId, ct);
            AccountAppService.EnsureConnectedForMarket(access);
        }

        // While a live trade is open for this user: do not analyze / place.
        if (await HasBlockingOpenTradeAsync(ct))
        {
            return SoftNone(asset.Trim(), periodSeconds) with
            {
                AutomationError = "OPEN_TRADE_EXISTS"
            };
        }

        var symbol = asset.Trim();
        var wirePeriod = BinollaMarketPeriods.NormalizeHistoryPeriod(periodSeconds);
        var client = await EnsureLiveClientAsync(ct);
        if (client is null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "RsiSignalAppService.GetSignalAsync",
                "rsi_soft_not_live",
                new { symbol, wirePeriod });
            // #endregion
            return SoftNone(symbol, wirePeriod);
        }

        client.EnsureMarketDataWarm(symbol, wirePeriod);
        try
        {
            var history = await client.GetHistoryAsync(symbol, wirePeriod, CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var period = TimeSpan.FromSeconds(wirePeriod);
            var candles = history.Candles
                .OrderBy(c => c.Timestamp)
                .Select(c =>
                {
                    var start = DateTimeOffset.FromUnixTimeMilliseconds((long)(c.Timestamp * 1000));
                    // Synthesize end when feed omits it so closed-candle filter can exclude the forming bar.
                    var end = c.EndTimestamp is null
                        ? start + period
                        : DateTimeOffset.FromUnixTimeMilliseconds((long)(c.EndTimestamp.Value * 1000));
                    return new RsiCandle(
                        Timestamp: start,
                        Close: (decimal)c.Close,
                        EndTimestamp: end);
                })
                .ToList();

            if (candles.Count == 0)
                return SoftNone(symbol, wirePeriod);

            _ = ApplyLiveQuoteToCandles(client, symbol, candles, wirePeriod, now);

            var signal = await _signalService.GetSignalAsync(
                userId: _currentUser.UserId,
                asset: symbol,
                candles: candles,
                options: options,
                now: now,
                ct: ct);
            return await ExecuteIfRequestedAsync(signal, options, autoExecute, ct);
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (BinollaTimeoutException)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "RsiSignalAppService.GetSignalAsync",
                "rsi_soft_timeout",
                new { symbol, wirePeriod });
            // #endregion
            return SoftNone(symbol, wirePeriod);
        }
        catch (OperationCanceledException)
        {
            return SoftNone(symbol, wirePeriod);
        }
        catch (BinollaConnectionException)
        {
            return SoftNone(symbol, wirePeriod);
        }
        catch (ApiException ex) when (ex.Code == ApiErrorCodes.ValidationError)
        {
            // Insufficient candles — soft None, not 400/500 spam.
            return SoftNone(symbol, wirePeriod);
        }
        catch (ApiException) { throw; }
        catch (Exception)
        {
            return SoftNone(symbol, wirePeriod);
        }
    }

    private static StrategySignal SoftNone(string symbol, int periodSeconds) =>
        new(
            StrategyId: "rsi",
            Asset: symbol,
            Signal: "None",
            Rsi: 0m,
            CandleTime: DateTimeOffset.UtcNow,
            Timeframe: periodSeconds.ToString());

    private async Task<StrategySignal> ExecuteIfRequestedAsync(
        StrategySignal signal,
        RsiStrategyOptions options,
        bool autoExecute,
        CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var bot = _botRuntime.Get(userId);
        // Home polls without autoExecute still place when Running — but only after full gate.
        if (bot.State != BotRunState.Running)
            return signal;

        var nowGate = DateTimeOffset.UtcNow;
        if (!RsiEntryLevels.TryValidateForTrade(signal, nowGate, out var rejectCode))
        {
            if (rejectCode is null or "NO_SIGNAL")
                return signal;
            return signal with { AutomationError = rejectCode, Signal = "None" };
        }

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-LIVE",
            "RsiSignalAppService.ExecuteIfRequestedAsync",
            "execute_live_setup",
            new
            {
                asset = signal.Asset,
                signal = signal.Signal,
                liveRsi = signal.LiveRsi,
                closedRsi = signal.Rsi,
                successRate = signal.Backtest?.SuccessRate,
                totalSignals = signal.Backtest?.TotalSignals,
                ageSec = Math.Round((nowGate - signal.CandleTime).TotalSeconds, 2),
                botState = bot.State.ToString(),
                autoExecute
            },
            runId: "missed-entry");
        // #endregion

        var selected = bot.ResolvedAssets;
        if (selected.Count > 0 && !BotAssetList.Contains(selected, signal.Asset))
            return signal with { AutomationError = "ASSET_NOT_SELECTED" };

        var dailyLimit = await GetReachedDailyLimitAsync(bot, ct);
        if (dailyLimit is not null)
        {
            _botRuntime.Stop(userId, dailyLimit);
            return signal with { AutomationError = dailyLimit };
        }

        // One live trade at a time — expired/stuck Running rows must not freeze the bot.
        var now = DateTimeOffset.UtcNow;
        var runningRows = await _tradeRepository.ListByUserAsync(
            userId, take: 20, status: TradeStatus.Running, ct: ct);
        var pendingRows = await _tradeRepository.ListByUserAsync(
            userId, take: 10, status: TradeStatus.Pending, ct: ct);
        var blocking = runningRows.Concat(pendingRows).Where(t => OpenTradeGate.IsBlocking(t, now)).ToList();
        if (blocking.Count > 0)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-STUCK1",
                "RsiSignalAppService.ExecuteIfRequestedAsync",
                "skip_open_trade_exists",
                new
                {
                    blocking = blocking.Count,
                    staleRunning = runningRows.Count - runningRows.Count(t => OpenTradeGate.IsBlocking(t, now)),
                    pending = pendingRows.Count,
                    asset = signal.Asset,
                    oldestAgeSec = blocking.Count == 0
                        ? 0
                        : Math.Round((now - blocking.Min(t => t.CreatedAt)).TotalSeconds, 0)
                },
                runId: "stuck-running");
            // #endregion
            return signal with { AutomationError = "OPEN_TRADE_EXISTS" };
        }

        if (runningRows.Count + pendingRows.Count > 0)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-STUCK1",
                "RsiSignalAppService.ExecuteIfRequestedAsync",
                "proceed_despite_stale_open",
                new
                {
                    staleRunning = runningRows.Count,
                    pending = pendingRows.Count,
                    asset = signal.Asset
                },
                runId: "stuck-running");
            // #endregion
        }

        var gate = await _autoTradeGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1))
            .WaitAsync(0, ct);
        if (!gate)
            return signal with { AutomationError = "AUTO_TRADE_BUSY" };

        try
        {
            runningRows = await _tradeRepository.ListByUserAsync(
                userId, take: 20, status: TradeStatus.Running, ct: ct);
            pendingRows = await _tradeRepository.ListByUserAsync(
                userId, take: 10, status: TradeStatus.Pending, ct: ct);
            if (runningRows.Concat(pendingRows).Any(t => OpenTradeGate.IsBlocking(t, DateTimeOffset.UtcNow)))
                return signal with { AutomationError = "OPEN_TRADE_EXISTS" };

            // Trade duration must match the backtest expiry window (3–5 minutes on 1m).
            var expiryCandles = signal.Backtest.ExpiryCandles;
            if (expiryCandles is < 3 or > 5)
                expiryCandles = options.ExpiryCandles is >= 3 and <= 5 ? options.ExpiryCandles : 5;
            var durationSeconds = expiryCandles * 60;

            var key = $"bot:rsi:{signal.Asset.Trim().ToUpperInvariant()}:{signal.CandleTime.ToUnixTimeSeconds()}:{signal.Signal}";
            try
            {
                var putOk = signal.LiveRsi is decimal lr && lr >= RsiEntryLevels.PutMin;
                var callOk = signal.LiveRsi is decimal lc && lc <= RsiEntryLevels.CallMax;
                var violation = (signal.Signal == "Put" && !putOk) || (signal.Signal == "Call" && !callOk);
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-C",
                    "RsiSignalAppService.ExecuteIfRequestedAsync",
                    "place_rsi_snapshot",
                    new
                    {
                        asset = signal.Asset,
                        signal = signal.Signal,
                        liveRsi = signal.LiveRsi,
                        closedRsi = signal.Rsi,
                        rsiEqClosed = signal.LiveRsi == signal.Rsi,
                        putOk,
                        callOk,
                        violation,
                        successRate = signal.Backtest?.SuccessRate
                    },
                    runId: "rsi-zone");
                // #endregion
                var trade = await _trades.PlaceTradeAsync(new PlaceTradeRequest(
                    Asset: signal.Asset,
                    Direction: signal.Signal.ToUpperInvariant(),
                    Amount: bot.Amount,
                    DurationSeconds: durationSeconds,
                    StrategyId: "rsi"), key, ct);

                _signalService.MarkSignalEmitted(
                    userId,
                    signal.Asset,
                    options.TimeframeSeconds,
                    signal.CandleTime);

                // #region agent log
                var tradeIdShort = trade.Id.Length >= 8 ? trade.Id[..8] : trade.Id;
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "OT1",
                    "RsiSignalAppService.ExecuteIfRequestedAsync",
                    "placed_one_trade",
                    new
                    {
                        tradeId = tradeIdShort,
                        asset = signal.Asset,
                        signal = signal.Signal,
                        durationSeconds,
                        successRate = signal.Backtest.SuccessRate
                    });
                // #endregion
                _logger.LogInformation(
                    "Placed RSI {Direction} {Asset} trade={TradeId} duration={Duration}s rate={Rate:F0}% liveRsi={LiveRsi} closedRsi={ClosedRsi}",
                    signal.Signal,
                    signal.Asset,
                    trade.Id,
                    durationSeconds,
                    signal.Backtest.SuccessRate,
                    signal.LiveRsi,
                    signal.Rsi);
                return signal with { AutomatedTradeId = trade.Id };
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(
                    "RSI place skipped {Asset} {Direction} code={Code}",
                    signal.Asset,
                    signal.Signal,
                    ex.Code);
                return signal with { AutomationError = ex.Code };
            }
        }
        finally
        {
            if (_autoTradeGates.TryGetValue(userId, out var sem))
                sem.Release();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _autoTradeGates = new();

    private async Task<string?> GetReachedDailyLimitAsync(BotRuntimeConfig bot, CancellationToken ct)
    {
        if ((!bot.AutoStopAtProfit || bot.DailyProfitTarget <= 0m) &&
            (!bot.AutoStopAtLoss || bot.DailyLossLimit <= 0m))
            return null;

        // Session baseline: after user presses Start again, prior PnL does not count.
        var startOfUtcDay = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var from = bot.PnlSessionStartedAt ?? startOfUtcDay;

        var trades = await _tradeRepository.ListByUserAsync(_currentUser.UserId, take: 1000, ct: ct);
        var pnl = trades
            .Where(trade =>
                trade.UpdatedAt >= from &&
                trade.Status is TradeStatus.Profit or TradeStatus.Loss)
            .Sum(trade => trade.Pnl ?? 0m);

        if (bot.AutoStopAtProfit && bot.DailyProfitTarget > 0m && pnl >= bot.DailyProfitTarget)
            return "DAILY_PROFIT_TARGET_REACHED";
        if (bot.AutoStopAtLoss && bot.DailyLossLimit > 0m && pnl <= -bot.DailyLossLimit)
            return "DAILY_LOSS_LIMIT_REACHED";

        return null;
    }

    private Task<IBinollaClient?> EnsureLiveClientAsync(CancellationToken ct)
    {
        var client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is not null &&
            client.IsTransportConnected &&
            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return Task.FromResult<IBinollaClient?>(client);
        }

        // Signal polling is latency-sensitive; reconnect without blocking this request.
        _restorer.EnsureBackgroundRestore(_currentUser.UserId);

        client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is not null &&
            client.IsTransportConnected &&
            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return Task.FromResult<IBinollaClient?>(client);
        }

        return Task.FromResult<IBinollaClient?>(null);
    }

    private async Task<bool> HasBlockingOpenTradeAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var running = await _tradeRepository.ListByUserAsync(
            _currentUser.UserId, take: 20, status: TradeStatus.Running, ct: ct);
        var pending = await _tradeRepository.ListByUserAsync(
            _currentUser.UserId, take: 10, status: TradeStatus.Pending, ct: ct);
        var blockers = running.Concat(pending).Where(t => OpenTradeGate.IsBlocking(t, now)).ToList();
        if (blockers.Count > 0)
        {
            var first = blockers.OrderBy(t => t.CreatedAt).First();
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-STUCK1",
                "RsiSignalAppService.HasBlockingOpenTradeAsync",
                "signal_skip_blocking_open",
                new
                {
                    blocking = blockers.Count,
                    running = running.Count,
                    pending = pending.Count,
                    asset = first.Asset,
                    direction = first.Direction.ToString(),
                    amount = first.Amount,
                    durationSec = first.DurationSeconds,
                    status = first.Status.ToString(),
                    ageSec = Math.Round((now - first.CreatedAt).TotalSeconds, 0),
                    tradeId = first.Id.ToString("N")[..8]
                },
                runId: "stuck-running");
            // #endregion
        }

        return blockers.Count > 0;
    }

    /// <summary>
    /// Overlay the latest tick as a forming candle so live RSI tracks price, not the last closed bar.
    /// Without a fresh quote, closed-only RSI must not drive entries.
    /// </summary>
    private static bool ApplyLiveQuoteToCandles(
        IBinollaClient client,
        string symbol,
        List<RsiCandle> candles,
        int wirePeriod,
        DateTimeOffset now)
    {
        const int maxQuoteAgeSeconds = 15;
        if (!client.TryGetCachedQuote(symbol, out var quote) || quote is null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-LIVE1",
                "RsiSignalAppService.ApplyLiveQuoteToCandles",
                "no_cached_quote",
                new { symbol },
                runId: "live-rsi");
            // #endregion
            return false;
        }

        var ageMs = (now - quote.ReceivedAt).TotalMilliseconds;
        if (ageMs > maxQuoteAgeSeconds * 1000)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-LIVE1",
                "RsiSignalAppService.ApplyLiveQuoteToCandles",
                "quote_too_stale",
                new { symbol, ageMs = Math.Round(ageMs, 0), price = quote.Price },
                runId: "live-rsi");
            // #endregion
            return false;
        }

        var period = TimeSpan.FromSeconds(wirePeriod);
        var bucketStartUnix = now.ToUnixTimeSeconds() / wirePeriod * wirePeriod;
        var bucketStart = DateTimeOffset.FromUnixTimeSeconds(bucketStartUnix);
        var price = (decimal)quote.Price;

        // Drop any bar that belongs to the current (still open) minute — replace with live quote.
        candles.RemoveAll(c =>
            c.Timestamp >= bucketStart ||
            c.EndTimestamp is null ||
            c.EndTimestamp > now);

        var lastClosedClose = candles.Count > 0 ? candles[^1].Close : (decimal?)null;
        candles.Add(new RsiCandle(
            Timestamp: bucketStart,
            Close: price,
            EndTimestamp: null));

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-LIVE1",
            "RsiSignalAppService.ApplyLiveQuoteToCandles",
            "forming_from_quote",
            new
            {
                symbol,
                price,
                lastClosedClose,
                ageMs = Math.Round(ageMs, 0),
                bucketStart = bucketStartUnix,
                closedCount = candles.Count - 1
            },
            runId: "live-rsi");
        // #endregion
        return true;
    }
}
