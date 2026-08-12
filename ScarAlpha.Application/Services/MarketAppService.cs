using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Application.Services;

public sealed class MarketAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBotAccessService _botAccess;
    private readonly ILogger<MarketAppService> _logger;

    public MarketAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        ILogger<MarketAppService> logger)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _botAccess = botAccess;
        _logger = logger;
    }

    public async Task<MarketAssetsResponse> GetAssetsAsync(CancellationToken ct)
    {
        await EnsureBotAccessAsync(ct);
        var client = RequireConnectedClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var assets = await client.GetTradingAssetsAsync(ct);
            _logger.LogInformation(
                "Market assets for user {UserId}: count={Count} elapsedMs={ElapsedMs}",
                _currentUser.UserId, assets.Count, sw.ElapsedMilliseconds);

            return new MarketAssetsResponse(assets.Select(a => new MarketAssetDto(
                Symbol: a.Symbol,
                Name: string.IsNullOrWhiteSpace(a.Description) ? a.Symbol : a.Description,
                Available: a.IsOpen,
                Payout: a.PayoutPercentage > 0 ? a.PayoutPercentage : null)).ToList());
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (BinollaTimeoutException)
        {
            throw new ApiException(ApiErrorCodes.MarketUnavailable, "Market assets are not available yet.", 503);
        }
        catch (BinollaConnectionException)
        {
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Binolla session is not connected.", 409);
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market assets failed for user {UserId}", _currentUser.UserId);
            throw new ApiException(ApiErrorCodes.BinollaConnectionFailed, "Unable to load market assets.", 502);
        }
    }

    public async Task<MarketPriceResponse> GetPriceAsync(string asset, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");

        await EnsureBotAccessAsync(ct);
        var client = RequireConnectedClient();
        var symbol = asset.Trim();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var quote = await client.GetLatestQuoteAsync(symbol, ct);
            _logger.LogInformation(
                "Market price for user {UserId} asset={Asset} elapsedMs={ElapsedMs}",
                _currentUser.UserId, symbol, sw.ElapsedMilliseconds);

            return new MarketPriceResponse(
                Asset: quote.Pair.Length > 0 ? quote.Pair : symbol,
                Price: (decimal)quote.Price,
                Timestamp: quote.QuoteTime);
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (BinollaTimeoutException)
        {
            throw new ApiException(ApiErrorCodes.MarketUnavailable, "Quote is not available for this asset.", 503);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ApiException(ApiErrorCodes.MarketUnavailable, "Quote request was cancelled.", 503);
        }
        catch (BinollaConnectionException)
        {
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Binolla session is not connected.", 409);
        }
        catch (BinollaOrderException ex)
        {
            throw new ApiException(ApiErrorCodes.ValidationError, ex.Message);
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market price failed for user {UserId}", _currentUser.UserId);
            throw new ApiException(ApiErrorCodes.BinollaConnectionFailed, "Unable to load quote.", 502);
        }
    }

    public async Task<MarketCandlesResponse> GetCandlesAsync(string asset, int period, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (period is < 1 or > 14400)
            throw new ApiException(ApiErrorCodes.ValidationError, "period must be between 1 and 14400 seconds.");

        await EnsureBotAccessAsync(ct);
        var client = RequireConnectedClient();
        var symbol = asset.Trim();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var history = await client.GetHistoryAsync(symbol, period, ct);
            var candles = history.Candles
                .Select(c => new MarketCandleDto(
                    Timestamp: DateTimeOffset.FromUnixTimeMilliseconds((long)(c.Timestamp * 1000)),
                    Open: (decimal)c.Open,
                    High: (decimal)c.High,
                    Low: (decimal)c.Low,
                    Close: (decimal)c.Close))
                .ToList();

            _logger.LogInformation(
                "Market candles for user {UserId} asset={Asset} period={Period} count={Count} elapsedMs={ElapsedMs}",
                _currentUser.UserId, symbol, period, candles.Count, sw.ElapsedMilliseconds);

            return new MarketCandlesResponse(symbol, period, candles);
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (BinollaTimeoutException)
        {
            throw new ApiException(ApiErrorCodes.MarketUnavailable, "Candles are not available for this asset.", 503);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ApiException(ApiErrorCodes.MarketUnavailable, "Candles request was cancelled.", 503);
        }
        catch (BinollaConnectionException)
        {
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Binolla session is not connected.", 409);
        }
        catch (BinollaOrderException ex)
        {
            throw new ApiException(ApiErrorCodes.ValidationError, ex.Message);
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market candles failed for user {UserId}", _currentUser.UserId);
            throw new ApiException(ApiErrorCodes.BinollaConnectionFailed, "Unable to load candles.", 502);
        }
    }

    private async Task EnsureBotAccessAsync(CancellationToken ct)
    {
        var access = await _botAccess.CheckAsync(_currentUser.UserId, ct);
        AccountAppService.EnsureConnectedForMarket(access);
    }

    private IBinollaClient RequireConnectedClient()
    {
        var client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is null ||
            client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
        {
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Connect Binolla before requesting market data.", 409);
        }

        return client;
    }
}
