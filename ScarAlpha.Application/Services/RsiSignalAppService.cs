using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
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

    public RsiSignalAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        IRsiSignalService signalService,
        IBinollaSessionRestorer restorer)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _botAccess = botAccess;
        _signalService = signalService;
        _restorer = restorer;
    }

    public async Task<StrategySignal> GetSignalAsync(
        string asset,
        int periodSeconds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (periodSeconds is < 1 or > 14400)
            throw new ApiException(ApiErrorCodes.ValidationError, "period must be between 1 and 14400 seconds.");

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

            var options = RsiStrategyOptions.Default60Seconds with { TimeframeSeconds = wirePeriod };
            return await _signalService.GetSignalAsync(
                userId: _currentUser.UserId,
                asset: symbol,
                candles: candles,
                options: options,
                now: DateTimeOffset.UtcNow,
                ct: ct);
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

    private async Task<IBinollaClient?> EnsureLiveClientAsync(CancellationToken ct)
    {
        var client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is not null &&
            client.IsTransportConnected &&
            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return client;
        }

        try
        {
            using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await _restorer.TryRestoreUserAsync(_currentUser.UserId, restoreCts.Token);
        }
        catch
        {
            // soft
        }

        client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is not null &&
            client.IsTransportConnected &&
            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return client;
        }

        return null;
    }
}
