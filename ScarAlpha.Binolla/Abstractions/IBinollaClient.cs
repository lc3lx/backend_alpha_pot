using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Binolla.Abstractions;

public interface IBinollaClient : IAsyncDisposable
{
    string UserId { get; }
    SessionLifecycleState Lifecycle { get; }

    event Action<SessionLifecycleState, string?>? LifecycleChanged;
    event Action? OnConnectionLost;
    event Action? OnReconnected;
    event Action<TradeOutcome>? OnOrderClosed;
    event Action? OnSessionExpired;

    Task ConnectAsync(string ssid, CancellationToken cancellationToken = default, string? cookieHeader = null);

    /// <summary>True when the trading WebSocket is open (not just Lifecycle.Connected).</summary>
    bool IsTransportConnected { get; }

    /// <summary>Compact wire diagnostics for market soft-miss logs (no secrets).</summary>
    string DescribeMarketWireState();

    Task<BalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default);

    Task ChangeAccountAsync(AccountType accountType, CancellationToken cancellationToken = default);

    Task<OrderResponse> PlaceOrderAsync(
        string asset,
        TradeDirection direction,
        decimal amount,
        int durationSeconds,
        CancellationToken cancellationToken = default);

    Task<TradeOutcome> WaitOutcomeAsync(string orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for Binolla close with an explicit timeout (use trade duration + buffer).
    /// </summary>
    Task<TradeOutcome> WaitOutcomeAsync(
        string orderId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task SubscribePairAsync(string pair, int period = 60, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fire-and-forget subscribe so the next HTTP poll can hit cache without blocking.
    /// </summary>
    void EnsureMarketDataWarm(string asset, int period = 60);

    Task<IReadOnlyList<TradingAsset>> GetTradingAssetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to the pair and return the latest pushed quote (waits up to DefaultOperationTimeout).
    /// </summary>
    Task<QuoteData> GetLatestQuoteAsync(string asset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to the pair/period and return the latest pushed history/candles.
    /// </summary>
    Task<HistoryData> GetHistoryAsync(string asset, int period, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IBinollaSessionManager : IAsyncDisposable
{
    Task<IBinollaClient> GetOrCreateAsync(
        string userId,
        string ssid,
        CancellationToken cancellationToken = default,
        string? cookieHeader = null);

    IBinollaClient? Get(string userId);

    Task RemoveAsync(string userId, CancellationToken cancellationToken = default);

    Task DisconnectAsync(string userId, CancellationToken cancellationToken = default);

    int ActiveSessionCount { get; }
}

public sealed class BinollaSessionManagerOptions
{
    /// <summary>Maximum simultaneous user sessions hosted in-process.</summary>
    public int MaxConcurrentSessions { get; set; } = 50;

    /// <summary>Idle sessions older than this are evicted.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan DefaultOperationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>WS auth should complete on post-auth events within seconds; fail fast if not.</summary>
    public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Background warm budget after a soft HTTP miss (not for blocking request path).</summary>
    /// <remarks>
    /// HTTP market calls use a short wait (~4s) then soft-empty; this timeout is for
    /// EnsureMarketDataWarm so history_stored can still populate cache for the next poll.
    /// </remarks>
    public TimeSpan MarketDataTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Max block for HTTP candles/price before soft-empty + background warm.</summary>
    public TimeSpan MarketHttpWait { get; set; } = TimeSpan.FromSeconds(4);

    public TimeSpan PlaceOrderTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fallback when caller does not pass a per-trade timeout.
    /// Must exceed longest common durations (5m) plus WS close lag — 5m raced 5m trades.
    /// </summary>
    public TimeSpan OutcomeTimeout { get; set; } = TimeSpan.FromMinutes(15);

    public bool EnableChartConnection { get; set; }

    public bool EnableAutoReconnect { get; set; } = true;

    public int MaxReconnectAttempts { get; set; } = 5;

    public Uri? TradingSocketUri { get; set; }

    public Uri? ChartSocketUri { get; set; }
}
