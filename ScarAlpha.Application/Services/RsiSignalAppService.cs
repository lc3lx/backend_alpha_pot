using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

public sealed class RsiSignalAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBotAccessService _botAccess;
    private readonly IRsiSignalService _signalService;

    public RsiSignalAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        IRsiSignalService signalService)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _botAccess = botAccess;
        _signalService = signalService;
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

        var client = RequireConnectedClient();

        var symbol = asset.Trim();
        var history = await client.GetHistoryAsync(symbol, periodSeconds, ct);

        var candles = history.Candles
            .Select(c => new RsiCandle(
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds((long)(c.Timestamp * 1000)),
                Close: (decimal)c.Close,
                EndTimestamp: c.EndTimestamp is null
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds((long)(c.EndTimestamp.Value * 1000))))
            .ToList();

        var options = RsiStrategyOptions.Default60Seconds with { TimeframeSeconds = periodSeconds };
        return await _signalService.GetSignalAsync(
            userId: _currentUser.UserId,
            asset: symbol,
            candles: candles,
            options: options,
            now: DateTimeOffset.UtcNow,
            ct: ct);
    }

    private IBinollaClient RequireConnectedClient()
    {
        var client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is null ||
            client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
        {
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Connect Binolla before requesting RSI signal.", 409);
        }

        return client;
    }
}

