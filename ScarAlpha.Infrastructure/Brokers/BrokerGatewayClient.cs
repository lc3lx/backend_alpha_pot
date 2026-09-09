using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Infrastructure.BrokerGateway;

/// <summary>
/// Talks to the Python broker gateway (<c>backend/brokers</c>) over localhost.
///
/// <para>Brokers whose usable client library is Python — Quotex, via QuotexAPI — live
/// behind that service rather than being reimplemented in C#. This class is the only place
/// in .NET that knows the gateway exists; everything above it sees an
/// <see cref="IBrokerClient"/> like any other.</para>
///
/// <para>Gateway statuses are mapped back to the codes the app already handles, so the
/// existing responses to a CAPTCHA, an IP block or an auth failure apply to every broker
/// without new branches upstream.</para>
/// </summary>
public sealed class BrokerGatewayClient : IBrokerClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Guid _userId;

    /// <summary>Quotes are cached so the hot path can read without a round trip.</summary>
    private readonly Dictionary<string, QuoteData> _quotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(asset, period) -> when candles were last confirmed present.</summary>
    private readonly Dictionary<string, DateTimeOffset> _warm = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, decimal> _closedPnl = new(StringComparer.Ordinal);

    public BrokerGatewayClient(
        HttpClient http,
        Guid userId,
        string broker,
        ILogger logger)
    {
        _http = http;
        _userId = userId;
        Broker = Brokers.Normalize(broker);
        _logger = logger;
        UserId = userId.ToString();
    }

    public string Broker { get; }

    public string UserId { get; }

    public SessionLifecycleState Lifecycle { get; private set; } = SessionLifecycleState.Disconnected;

    public bool IsTransportConnected { get; private set; }

    /// <summary>Opens the session on the gateway. Called by the session manager, not by app code.</summary>
    internal async Task ConnectAsync(BrokerCredentials credentials, CancellationToken ct)
    {
        Lifecycle = SessionLifecycleState.Connecting;

        using var deadline = Deadline(ct, TimeSpan.FromSeconds(90));
        var response = await _http.PostAsJsonAsync(
            "/sessions/connect",
            new
            {
                user_id = UserId,
                broker = Broker,
                ssid = credentials.Ssid,
                email = credentials.Email,
                password = credentials.Password,
                account_type = credentials.AccountType.ToString()
            },
            Json,
            deadline.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Lifecycle = SessionLifecycleState.AuthenticationFailed;
            throw await TranslateAsync(response, ct).ConfigureAwait(false);
        }

        Lifecycle = SessionLifecycleState.Connected;
        IsTransportConnected = true;
    }

    public async Task<BalanceInfo> GetBalanceAsync(CancellationToken ct = default)
    {
        var dto = await GetAsync<BalanceDto>("/account/balance", null, ct).ConfigureAwait(false);
        var type = dto.CurrentType == AccountType.Demo ? AccountType.Demo : AccountType.Real;
        return new BalanceInfo
        {
            DemoBalance = dto.Demo,
            RealBalance = dto.Real,
            CurrentType = type,
            LastUpdated = DateTimeOffset.UtcNow,
            Currency = "USD"
        };
    }

    public async Task ChangeAccountAsync(AccountType accountType, CancellationToken ct = default)
    {
        // Re-connecting is how the balance is switched: the gateway applies the account
        // type during the handshake, because switching afterwards races the broker's own
        // setup and can leave the socket on the other balance.
        using var deadline = Deadline(ct, TimeSpan.FromSeconds(90));
        var response = await _http.PostAsync(
            $"/sessions/connect?user_id={Uri.EscapeDataString(UserId)}&broker={Broker}"
            + $"&account_type={accountType}",
            content: null,
            deadline.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await TranslateAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TradingAsset>> GetTradingAssetsAsync(CancellationToken ct = default)
    {
        var dtos = await GetAsync<List<AssetDto>>("/market/assets", null, ct).ConfigureAwait(false);
        return dtos.Select(a => new TradingAsset
        {
            Symbol = a.Symbol,
            Description = a.Name ?? a.Symbol,
            IsOpen = a.IsOpen,
            PayoutPercentage = a.Payout
        }).ToList();
    }

    public async Task<HistoryData> GetHistoryAsync(
        string asset, int periodSeconds, CancellationToken ct = default)
    {
        var query = $"asset={Uri.EscapeDataString(asset)}&period_seconds={periodSeconds}&count=300";
        var candles = await GetAsync<List<CandleDto>>("/market/candles", query, ct).ConfigureAwait(false);

        var history = new HistoryData
        {
            Asset = asset,
            Period = periodSeconds,
            ReceivedAt = DateTimeOffset.UtcNow,
            AccessedAt = DateTimeOffset.UtcNow
        };

        foreach (var c in candles)
        {
            history.Candles.Add(new CandlestickData
            {
                Timestamp = c.Timestamp,
                Open = (double)c.Open,
                High = (double)c.High,
                Low = (double)c.Low,
                Close = (double)c.Close,
                Volume = c.Volume is null ? null : (double)c.Volume,
                EndTimestamp = c.Timestamp + periodSeconds
            });
        }

        if (candles.Count > 0)
            _warm[WarmKey(asset, periodSeconds)] = DateTimeOffset.UtcNow;

        return history;
    }

    public async Task<QuoteData> GetLatestQuoteAsync(string asset, CancellationToken ct = default)
    {
        var query = $"asset={Uri.EscapeDataString(asset)}";
        var dto = await GetAsync<QuoteDto?>("/market/quote", query, ct).ConfigureAwait(false);
        if (dto is null)
            throw new ApiException(ApiErrorCodes.MarketUnavailable, $"No quote for {asset}.", 503);

        var quote = new QuoteData
        {
            Pair = asset,
            Timestamp = dto.Timestamp,
            Price = (double)dto.Price,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        _quotes[asset] = quote;
        return quote;
    }

    public bool TryGetCachedQuote(string asset, out QuoteData? quote) =>
        _quotes.TryGetValue(asset, out quote);

    public async Task SubscribePairAsync(
        string asset, int periodSeconds = 60, CancellationToken ct = default)
    {
        var query = $"user_id={Uri.EscapeDataString(UserId)}&broker={Broker}"
                    + $"&asset={Uri.EscapeDataString(asset)}&period_seconds={periodSeconds}";
        using var deadline = Deadline(ct);
        var response = await _http
            .PostAsync($"/market/subscribe?{query}", content: null, deadline.Token)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            _warm[WarmKey(asset, periodSeconds)] = DateTimeOffset.UtcNow;
    }

    public void EnsureMarketDataWarm(string asset, int periodSeconds = 60)
    {
        if (HasFreshHistory(asset, periodSeconds))
            return;

        // Fire and forget, exactly like the Binolla client: warming must never block the
        // entry path, and the next sweep retries anything that failed.
        _ = Task.Run(async () =>
        {
            try
            {
                await SubscribePairAsync(asset, periodSeconds, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Broker gateway warm failed for {Asset}", asset);
            }
        });
    }

    public bool HasFreshHistory(string asset, int periodSeconds)
    {
        if (!_warm.TryGetValue(WarmKey(asset, periodSeconds), out var seen))
            return false;
        // One bar of slack; past that the stream is not keeping the series current.
        return (DateTimeOffset.UtcNow - seen).TotalSeconds < periodSeconds + 15;
    }

    public async Task<OrderResponse> PlaceOrderAsync(
        string asset,
        TradeDirection direction,
        decimal amount,
        int durationSeconds,
        CancellationToken ct = default)
    {
        // An order is time-critical: if it cannot go out promptly it must fail, not sit
        // in a socket while the bar it was priced on passes.
        using var deadline = Deadline(ct, TimeSpan.FromSeconds(20));
        var response = await _http.PostAsJsonAsync(
            "/orders",
            new
            {
                user_id = UserId,
                broker = Broker,
                asset,
                amount = (double)amount,
                duration_seconds = durationSeconds,
                direction = direction == TradeDirection.Call ? "CALL" : "PUT"
            },
            Json,
            deadline.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await TranslateAsync(response, ct).ConfigureAwait(false);

        var dto = await response.Content.ReadFromJsonAsync<OrderDto>(Json, ct).ConfigureAwait(false)
                  ?? throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Gateway returned no order.", 502);

        return new OrderResponse
        {
            OrderId = dto.OrderId,
            Asset = dto.Asset,
            Direction = direction,
            Amount = amount,
            OpenPrice = dto.OpenPrice ?? 0m,
            PlacedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(dto.PlacedAt * 1000)),
            ExpiryTime = DateTimeOffset.FromUnixTimeMilliseconds(
                (long)((dto.ExpiryAt ?? dto.PlacedAt + durationSeconds) * 1000)),
            Status = OrderStatus.Open,
            BalanceType = dto.AccountType
        };
    }

    public Task<TradeOutcome> WaitOutcomeAsync(string orderId, CancellationToken ct = default) =>
        WaitOutcomeAsync(orderId, TimeSpan.FromSeconds(120), ct);

    public async Task<TradeOutcome> WaitOutcomeAsync(
        string orderId, TimeSpan timeout, CancellationToken ct = default)
    {
        // Kept under the HttpClient's own 45s ceiling would be wrong the other way: the
        // gateway holds the request open for the whole wait, so the socket timeout has to
        // be the larger of the two or a long expiry is cut off mid-wait.
        var seconds = (int)Math.Clamp(timeout.TotalSeconds, 5, 3600);
        var query = $"order_id={Uri.EscapeDataString(orderId)}&timeout_seconds={seconds}";
        // Slack over the gateway's own wait, so it is the gateway that decides the answer
        // is not coming rather than the socket giving up first.
        var dto = await GetAsync<OutcomeDto>(
            "/orders/outcome", query, ct, TimeSpan.FromSeconds(seconds + 15)).ConfigureAwait(false);

        var result = dto.Result switch
        {
            "Win" => TradeResult.Win,
            "Loss" => TradeResult.Loss,
            "Tie" => TradeResult.Tie,
            // Pending is this enum's "not decided". Mapping an unknown outcome to Loss
            // would report a winning trade as a loss, which is worse than saying we do
            // not know yet — the outcome worker re-checks Pending, it does not re-check
            // a settled Loss.
            _ => TradeResult.Pending
        };

        _closedPnl[orderId] = dto.ProfitLoss;

        return new TradeOutcome
        {
            OrderId = orderId,
            Result = result,
            ProfitLoss = dto.ProfitLoss,
            ClosePrice = dto.ClosePrice,
            ClosedAt = DateTimeOffset.UtcNow
        };
    }

    public bool TryGetClosedPnl(string orderId, out decimal profitLoss) =>
        _closedPnl.TryGetValue(orderId, out profitLoss);

    public string DescribeState() =>
        $"{Broker} gateway lifecycle={Lifecycle} transport={IsTransportConnected} "
        + $"warmKeys={_warm.Count} quotes={_quotes.Count}";

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        IsTransportConnected = false;
        Lifecycle = SessionLifecycleState.Disconnected;
        try
        {
            var query = $"user_id={Uri.EscapeDataString(UserId)}&broker={Broker}";
            using var deadline = Deadline(ct, TimeSpan.FromSeconds(15));
            await _http
                .PostAsync($"/sessions/disconnect?{query}", content: null, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The local state is already torn down; a failing call on a dead session is
            // not worth surfacing to the caller.
            _logger.LogDebug(ex, "Broker gateway disconnect failed for {UserId}", UserId);
        }
    }

    /// <summary>
    /// Default budget for an ordinary call. Long enough for the gateway's own retries,
    /// short enough that a stalled broker cannot hold a worker tick open.
    /// </summary>
    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Bounds one request. The HttpClient's own timeout is disabled precisely so that
    /// each call can choose, so every request must pass through one of these.
    /// </summary>
    private static CancellationTokenSource Deadline(CancellationToken ct, TimeSpan? budget = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget ?? CallBudget);
        return cts;
    }

    private async Task<T> GetAsync<T>(
        string path, string? query, CancellationToken ct, TimeSpan? budget = null)
    {
        var url = $"{path}?user_id={Uri.EscapeDataString(UserId)}&broker={Broker}"
                  + (string.IsNullOrEmpty(query) ? string.Empty : "&" + query);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget ?? CallBudget);

        var response = await _http.GetAsync(url, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await TranslateAsync(response, ct).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
               ?? throw new ApiException(ApiErrorCodes.MarketUnavailable, "Gateway returned no body.", 502);
    }

    /// <summary>
    /// Maps a gateway status onto the codes the app already reacts to, so the handling
    /// built for Binolla's blocks and CAPTCHAs covers every broker unchanged.
    /// </summary>
    private async Task<ApiException> TranslateAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var detail = await SafeReadAsync(response, ct).ConfigureAwait(false);

        return response.StatusCode switch
        {
            // 428: a human check. Intermittent — the caller should retry, not tell the
            // user their password is wrong.
            HttpStatusCode.PreconditionRequired => new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                $"{Broker} asked for a CAPTCHA. It is intermittent — the next attempt usually "
                + "succeeds, or paste an SSID from the broker to connect right now.",
                400),

            // 451: the broker refused the SERVER. Not per-account.
            HttpStatusCode.UnavailableForLegalReasons => new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                $"{Broker} refused the login from this server (IP/location block, not a wrong "
                + "password). Set a proxy in an allowed country, or paste an SSID.",
                400),

            HttpStatusCode.TooManyRequests => new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "A login attempt is already running or was just refused. Wait a moment before trying again.",
                429),

            HttpStatusCode.Unauthorized => new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                $"{Broker} rejected the credentials.",
                400),

            HttpStatusCode.Conflict => new ApiException(
                ApiErrorCodes.BinollaNotConnected,
                $"No live {Broker} session. Connect first.",
                409),

            _ => new ApiException(
                ApiErrorCodes.BinollaNotConnected,
                $"{Broker} gateway error ({(int)response.StatusCode}). {detail}".Trim(),
                502)
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 200 ? body[..200] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string WarmKey(string asset, int periodSeconds) =>
        $"{asset}:{periodSeconds}";

    // ---- gateway wire shapes ----------------------------------------------

    private sealed record BalanceDto(
        decimal Demo,
        decimal Real,
        [property: JsonPropertyName("current_type")] AccountType CurrentType);

    private sealed record AssetDto(
        string Symbol,
        string? Name,
        [property: JsonPropertyName("is_open")] bool IsOpen,
        int Payout);

    private sealed record CandleDto(
        long Timestamp, decimal Open, decimal High, decimal Low, decimal Close, decimal? Volume);

    private sealed record QuoteDto(string Asset, double Timestamp, decimal Price);

    private sealed record OrderDto(
        [property: JsonPropertyName("order_id")] string OrderId,
        string Asset,
        string Direction,
        decimal Amount,
        [property: JsonPropertyName("open_price")] decimal? OpenPrice,
        [property: JsonPropertyName("placed_at")] double PlacedAt,
        [property: JsonPropertyName("expiry_at")] double? ExpiryAt,
        [property: JsonPropertyName("account_type")] AccountType AccountType);

    private sealed record OutcomeDto(
        [property: JsonPropertyName("order_id")] string OrderId,
        string Result,
        [property: JsonPropertyName("profit_loss")] decimal ProfitLoss,
        [property: JsonPropertyName("close_price")] decimal? ClosePrice);
}
