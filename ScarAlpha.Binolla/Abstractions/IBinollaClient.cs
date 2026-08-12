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

    Task<BalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default);

    Task ChangeAccountAsync(AccountType accountType, CancellationToken cancellationToken = default);

    Task<OrderResponse> PlaceOrderAsync(
        string asset,
        TradeDirection direction,
        decimal amount,
        int durationSeconds,
        CancellationToken cancellationToken = default);

    Task<TradeOutcome> WaitOutcomeAsync(string orderId, CancellationToken cancellationToken = default);

    Task SubscribePairAsync(string pair, int period = 60, CancellationToken cancellationToken = default);

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

    public TimeSpan PlaceOrderTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan OutcomeTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public bool EnableChartConnection { get; set; }

    public bool EnableAutoReconnect { get; set; } = true;

    public int MaxReconnectAttempts { get; set; } = 5;

    public Uri? TradingSocketUri { get; set; }

    public Uri? ChartSocketUri { get; set; }
}
