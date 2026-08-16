using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;

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

    public RsiSignalAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        IRsiSignalService signalService,
        IBinollaSessionRestorer restorer,
        IMarketingDemoService demo,
        TradeAppService trades)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _botAccess = botAccess;
        _signalService = signalService;
        _restorer = restorer;
        _demo = demo;
        _trades = trades;
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
        if (!autoExecute || signal.Signal is not ("Call" or "Put"))
            return signal;

        var key = $"bot:rsi:{signal.Asset.Trim().ToUpperInvariant()}:{signal.CandleTime.ToUnixTimeSeconds()}:{signal.Signal}";
        try
        {
            var trade = await _trades.PlaceTradeAsync(new PlaceTradeRequest(
                Asset: signal.Asset,
                Direction: signal.Signal.ToUpperInvariant(),
                Amount: 25m,
                DurationSeconds: 300,
                StrategyId: "rsi"), key, ct);
            return signal with { AutomatedTradeId = trade.Id };
        }
        catch (ApiException ex)
        {
            return signal with { AutomationError = ex.Code };
        }
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
