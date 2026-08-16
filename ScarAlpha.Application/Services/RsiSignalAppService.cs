using System.Collections.Concurrent;
using System.Threading;
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

    public RsiSignalAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        IRsiSignalService signalService,
        IBinollaSessionRestorer restorer,
        IMarketingDemoService demo,
        TradeAppService trades,
        IBotRuntimeService botRuntime,
        ITradeRepository tradeRepository)
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
    }

    public async Task<StrategySignal> GetSignalAsync(
        string asset,
        int periodSeconds,
        RsiStrategyOptions options,
        bool autoExecute,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (periodSeconds != 60)
            throw new ApiException(ApiErrorCodes.ValidationError, "RSI Smart Backtest only supports the 1-minute timeframe.");

        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return await ExecuteIfRequestedAsync(_demo.BuildRsiSignal(asset, periodSeconds), autoExecute, ct);

        var access = await _botAccess.CheckAsync(_currentUser.UserId, ct);
        AccountAppService.EnsureConnectedForMarket(access);

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

            var candles = history.Candles
                .OrderBy(c => c.Timestamp)
                .Select(c => new RsiCandle(
                    Timestamp: DateTimeOffset.FromUnixTimeMilliseconds((long)(c.Timestamp * 1000)),
                    Close: (decimal)c.Close,
                    EndTimestamp: c.EndTimestamp is null
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds((long)(c.EndTimestamp.Value * 1000))))
                .ToList();

            if (candles.Count == 0)
                return SoftNone(symbol, wirePeriod);

            var signal = await _signalService.GetSignalAsync(
                userId: _currentUser.UserId,
                asset: symbol,
                candles: candles,
                options: options,
                now: DateTimeOffset.UtcNow,
                ct: ct);
            return await ExecuteIfRequestedAsync(signal, autoExecute, ct);
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
        bool autoExecute,
        CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var bot = _botRuntime.Get(userId);
        if (!autoExecute || bot.State != BotRunState.Running || signal.Signal is not ("Call" or "Put"))
            return signal;

        var selected = bot.ResolvedAssets;
        if (selected.Count > 0 && !BotAssetList.Contains(selected, signal.Asset))
            return signal;

        var dailyLimit = await GetReachedDailyLimitAsync(bot, ct);
        if (dailyLimit is not null)
        {
            _botRuntime.Stop(userId);
            return signal with { AutomationError = dailyLimit };
        }

        // One open trade at a time — never stack 2+ concurrent bot trades.
        var openCount = await _tradeRepository.CountByUserAsync(
            userId, TradeStatus.Running, ct: ct);
        var pendingCount = await _tradeRepository.CountByUserAsync(
            userId, TradeStatus.Pending, ct: ct);
        if (openCount + pendingCount > 0)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "OT1",
                "RsiSignalAppService.ExecuteIfRequestedAsync",
                "skip_open_trade_exists",
                new { openCount, pendingCount, asset = signal.Asset });
            // #endregion
            return signal with { AutomationError = "OPEN_TRADE_EXISTS" };
        }

        var gate = await _autoTradeGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1))
            .WaitAsync(0, ct);
        if (!gate)
            return signal with { AutomationError = "AUTO_TRADE_BUSY" };

        try
        {
            openCount = await _tradeRepository.CountByUserAsync(userId, TradeStatus.Running, ct: ct);
            pendingCount = await _tradeRepository.CountByUserAsync(userId, TradeStatus.Pending, ct: ct);
            if (openCount + pendingCount > 0)
                return signal with { AutomationError = "OPEN_TRADE_EXISTS" };

            var key = $"bot:rsi:{signal.Asset.Trim().ToUpperInvariant()}:{signal.CandleTime.ToUnixTimeSeconds()}:{signal.Signal}";
            try
            {
                var trade = await _trades.PlaceTradeAsync(new PlaceTradeRequest(
                    Asset: signal.Asset,
                    Direction: signal.Signal.ToUpperInvariant(),
                    Amount: bot.Amount,
                    DurationSeconds: bot.DurationSeconds,
                    StrategyId: "rsi"), key, ct);
                // #region agent log
                var tradeIdShort = trade.Id.Length >= 8 ? trade.Id[..8] : trade.Id;
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "OT1",
                    "RsiSignalAppService.ExecuteIfRequestedAsync",
                    "placed_one_trade",
                    new { tradeId = tradeIdShort, asset = signal.Asset, signal = signal.Signal });
                // #endregion
                return signal with { AutomatedTradeId = trade.Id };
            }
            catch (ApiException ex)
            {
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

        var startOfUtcDay = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var trades = await _tradeRepository.ListByUserAsync(_currentUser.UserId, take: 1000, ct: ct);
        var pnl = trades
            .Where(trade =>
                trade.UpdatedAt >= startOfUtcDay &&
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
}
