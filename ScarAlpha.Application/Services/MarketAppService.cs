using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;

namespace ScarAlpha.Application.Services;

public sealed class MarketAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBotAccessService _botAccess;
    private readonly IBinollaSessionRestorer _restorer;
    private readonly IMarketingDemoService _demo;
    private readonly ILogger<MarketAppService> _logger;

    public MarketAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        IBinollaSessionRestorer restorer,
        IMarketingDemoService demo,
        ILogger<MarketAppService> logger)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _botAccess = botAccess;
        _restorer = restorer;
        _demo = demo;
        _logger = logger;
    }

    public async Task<MarketAssetsResponse> GetAssetsAsync(CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.BuildAssets();

        await EnsureBotAccessAsync(ct);
        var client = await EnsureLiveClientAsync(ct);
        if (client is null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H125",
                "MarketAppService.GetAssetsAsync",
                "not_live_soft_empty",
                new { });
            // #endregion
            return new MarketAssetsResponse(Array.Empty<MarketAssetDto>());
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var assets = await client.GetTradingAssetsAsync(CancellationToken.None);
            _logger.LogInformation(
                "Market assets for user {UserId}: count={Count} elapsedMs={ElapsedMs}",
                _currentUser.UserId, assets.Count, sw.ElapsedMilliseconds);

            // Do not prefetch EURUSD here — every Home assets poll was stealing the
            // quote stream from the worker's rotating batch via asset/change.

            // #region agent log
            var fxLike = assets.Where(a =>
                System.Text.RegularExpressions.Regex.IsMatch(
                    a.Symbol.Replace("/", "", StringComparison.Ordinal),
                    @"^(EUR|GBP|USD|AUD|CAD|CHF|JPY|NZD){2}(_otc)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H1",
                "MarketAppService.GetAssetsAsync",
                "assets_list",
                new
                {
                    total = assets.Count,
                    open = assets.Count(a => a.IsOpen),
                    fxCount = fxLike.Count,
                    sample = assets.Take(8).Select(a => a.Symbol).ToArray(),
                    fxSample = fxLike.Take(12).Select(a => a.Symbol).ToArray(),
                    hasEurUsd = assets.Any(a => a.Symbol.Contains("EURUSD", StringComparison.OrdinalIgnoreCase))
                });
            // #endregion

            return new MarketAssetsResponse(assets.Select(a => new MarketAssetDto(
                Symbol: a.Symbol,
                Name: string.IsNullOrWhiteSpace(a.Description) ? a.Symbol : a.Description,
                Available: a.IsOpen,
                Payout: a.PayoutPercentage > 0 ? a.PayoutPercentage : null)).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new MarketAssetsResponse(Array.Empty<MarketAssetDto>());
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market assets failed for user {UserId}", _currentUser.UserId);
            return new MarketAssetsResponse(Array.Empty<MarketAssetDto>());
        }
    }

    public async Task<MarketPriceResponse> GetPriceAsync(string asset, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");

        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.BuildPrice(asset);

        await EnsureBotAccessAsync(ct);
        var client = await EnsureLiveClientAsync(ct);
        var symbol = asset.Trim();
        if (client is null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "MarketAppService.GetPriceAsync",
                "price_soft_not_live",
                new { symbol });
            // #endregion
            return new MarketPriceResponse(symbol, null, DateTimeOffset.UtcNow);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var quote = await client.GetLatestQuoteAsync(symbol, CancellationToken.None);
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
            client.EnsureMarketDataWarm(symbol, 60);
            _logger.LogInformation(
                "Market price not ready for user {UserId} asset={Asset}; returning soft null",
                _currentUser.UserId, symbol);
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "MarketAppService.GetPriceAsync",
                "quote_soft_timeout",
                new { symbol, elapsedMs = sw.ElapsedMilliseconds });
            // #endregion
            // Soft 200 — never 5xx ERR that kills the chart shell.
            return new MarketPriceResponse(symbol, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            client.EnsureMarketDataWarm(symbol, 60);
            _logger.LogInformation(
                "Market price canceled for user {UserId} asset={Asset}; returning soft null",
                _currentUser.UserId, symbol);
            return new MarketPriceResponse(symbol, null, DateTimeOffset.UtcNow);
        }
        catch (BinollaConnectionException)
        {
            return new MarketPriceResponse(symbol, null, DateTimeOffset.UtcNow);
        }
        catch (BinollaOrderException ex)
        {
            throw new ApiException(ApiErrorCodes.ValidationError, ex.Message);
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market price failed for user {UserId}", _currentUser.UserId);
            return new MarketPriceResponse(symbol, null, DateTimeOffset.UtcNow);
        }
    }

    public async Task<MarketCandlesResponse> GetCandlesAsync(string asset, int period, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (period is < 1 or > 14400)
            throw new ApiException(ApiErrorCodes.ValidationError, "period must be between 1 and 14400 seconds.");

        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.BuildCandles(asset, period);

        await EnsureBotAccessAsync(ct);
        var client = await EnsureLiveClientAsync(ct);
        var symbol = asset.Trim();
        var wirePeriod = BinollaMarketPeriods.NormalizeHistoryPeriod(period);
        if (client is null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "MarketAppService.GetCandlesAsync",
                "candles_soft_not_live",
                new { symbol, period, wirePeriod });
            // #endregion
            return new MarketCandlesResponse(symbol, wirePeriod, Array.Empty<MarketCandleDto>());
        }

        client.EnsureMarketDataWarm(symbol, wirePeriod);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var history = await client.GetHistoryAsync(symbol, wirePeriod, CancellationToken.None);
            var raw = history.Candles;
            var rawFirstTs = raw.Count > 0 ? raw[0].Timestamp : (double?)null;
            var rawLastTs = raw.Count > 0 ? raw[^1].Timestamp : (double?)null;
            var wasNewestFirst = rawFirstTs is not null && rawLastTs is not null && rawFirstTs > rawLastTs;

            var candles = raw
                .OrderBy(c => c.Timestamp)
                .Select(c => new MarketCandleDto(
                    Timestamp: DateTimeOffset.FromUnixTimeMilliseconds((long)(c.Timestamp * 1000)),
                    Open: (decimal)c.Open,
                    High: (decimal)c.High,
                    Low: (decimal)c.Low,
                    Close: (decimal)c.Close))
                .ToList();

            var up = candles.Count(c => c.Close > c.Open);
            var down = candles.Count(c => c.Close < c.Open);
            var doji = candles.Count(c => c.Close == c.Open);
            var tail = candles.TakeLast(3).Select(c =>
                $"{c.Open:F5}/{c.High:F5}/{c.Low:F5}/{c.Close:F5}").ToArray();

            var responsePeriod = history.Period > 0 ? history.Period : wirePeriod;
            _logger.LogInformation(
                "Market candles for user {UserId} asset={Asset} period={Period} count={Count} up={Up} down={Down} doji={Doji} sample={Sample} elapsedMs={ElapsedMs}",
                _currentUser.UserId, symbol, responsePeriod, candles.Count, up, down, doji,
                string.Join(" | ", tail), sw.ElapsedMilliseconds);

            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H91",
                "MarketAppService.GetCandlesAsync",
                "candle_order",
                new
                {
                    count = candles.Count,
                    rawFirstTs,
                    rawLastTs,
                    wasNewestFirst,
                    sortedFirstTs = candles.Count > 0 ? candles[0].Timestamp.ToUnixTimeSeconds() : (long?)null,
                    sortedLastTs = candles.Count > 0 ? candles[^1].Timestamp.ToUnixTimeSeconds() : (long?)null,
                    requestedPeriod = period,
                    wirePeriod,
                    responsePeriod,
                    symbol
                });
            // #endregion

            return new MarketCandlesResponse(symbol, responsePeriod, candles);
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (BinollaTimeoutException)
        {
            // Soft empty — FE retries; hard 500 blocked the chart shell after assets already worked.
            var wire = client.DescribeMarketWireState();
            _logger.LogInformation(
                "Market candles not ready for user {UserId} asset={Asset} period={Period}; returning empty; wire={Wire}",
                _currentUser.UserId, symbol, wirePeriod, wire);
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "MarketAppService.GetCandlesAsync",
                "candles_soft_timeout",
                new { symbol, period, wirePeriod, elapsedMs = sw.ElapsedMilliseconds, wire });
            // #endregion
            return new MarketCandlesResponse(symbol, wirePeriod, Array.Empty<MarketCandleDto>());
        }
        catch (OperationCanceledException)
        {
            // Timeout mis-classified as cancel, or client aborted mid-wait — never 500 the chart.
            _logger.LogInformation(
                "Market candles canceled/empty for user {UserId} asset={Asset} period={Period}",
                _currentUser.UserId, symbol, wirePeriod);
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "MarketAppService.GetCandlesAsync",
                "candles_soft_cancel",
                new { symbol, period, wirePeriod, elapsedMs = sw.ElapsedMilliseconds, httpAborted = ct.IsCancellationRequested });
            // #endregion
            return new MarketCandlesResponse(symbol, wirePeriod, Array.Empty<MarketCandleDto>());
        }
        catch (BinollaConnectionException)
        {
            return new MarketCandlesResponse(symbol, wirePeriod, Array.Empty<MarketCandleDto>());
        }
        catch (BinollaOrderException ex)
        {
            throw new ApiException(ApiErrorCodes.ValidationError, ex.Message);
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market candles failed for user {UserId}", _currentUser.UserId);
            return new MarketCandlesResponse(symbol, wirePeriod, Array.Empty<MarketCandleDto>());
        }
    }

    private async Task EnsureBotAccessAsync(CancellationToken ct)
    {
        var access = await _botAccess.CheckAsync(_currentUser.UserId, ct);
        AccountAppService.EnsureConnectedForMarket(access);
    }

    /// <summary>
    /// Prefer a live in-memory session. Restore is background-only so a rejected upstream
    /// credential never turns browser polling into a socket-handshake wait.
    /// </summary>
    private Task<IBinollaClient?> EnsureLiveClientAsync(CancellationToken ct)
    {
        var live = FindLiveClient();
        if (live is not null)
            return Task.FromResult<IBinollaClient?>(live);

        _restorer.EnsureBackgroundRestore(_currentUser.UserId);
        return Task.FromResult<IBinollaClient?>(null);
    }

    private IBinollaClient? FindLiveClient()
    {
        var client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is not null &&
            client.IsTransportConnected &&
            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return client;
        }

        return null;
    }
}
