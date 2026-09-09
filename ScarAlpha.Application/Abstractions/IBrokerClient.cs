using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Application.Abstractions;

/// <summary>Brokers the bot can trade through.</summary>
public static class Brokers
{
    public const string Binolla = "binolla";
    public const string Quotex = "quotex";

    /// <summary>What a link with no broker recorded means — every account predates the choice.</summary>
    public const string Default = Binolla;

    public static readonly IReadOnlyList<string> All = new[] { Binolla, Quotex };

    /// <summary>Canonical id, or the default when the value is missing or unknown.</summary>
    public static string Normalize(string? broker)
    {
        var value = (broker ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(value) ? value : Default;
    }

    public static bool IsKnown(string? broker) =>
        All.Contains((broker ?? string.Empty).Trim().ToLowerInvariant());
}

/// <summary>
/// One user's live connection to one broker.
///
/// <para>This is the surface the bot actually uses, lifted out of <see cref="IBinollaClient"/>
/// so the strategy and worker layers stop naming a specific venue. The model types
/// (<see cref="BalanceInfo"/>, <see cref="TradingAsset"/>, <see cref="QuoteData"/>, …) are
/// plain DTOs with nothing broker-specific in them, so they carry over unchanged — only
/// the client is being abstracted.</para>
///
/// <para>Deliberately narrower than <see cref="IBinollaClient"/>: the events and duplicate
/// overloads there are Binolla's own reconnect plumbing, which each implementation owns
/// privately rather than exposing.</para>
/// </summary>
public interface IBrokerClient
{
    /// <summary>Which venue this client speaks to — one of <see cref="Brokers"/>.</summary>
    string Broker { get; }

    string UserId { get; }

    SessionLifecycleState Lifecycle { get; }

    bool IsTransportConnected { get; }

    Task<BalanceInfo> GetBalanceAsync(CancellationToken ct = default);

    Task ChangeAccountAsync(AccountType accountType, CancellationToken ct = default);

    Task<IReadOnlyList<TradingAsset>> GetTradingAssetsAsync(CancellationToken ct = default);

    /// <summary>
    /// Closed bars for one pair. Implementations must never include the forming bar —
    /// that is what makes an indicator disagree with the broker's own chart, and no
    /// downstream calibration can correct it.
    /// </summary>
    Task<HistoryData> GetHistoryAsync(string asset, int periodSeconds, CancellationToken ct = default);

    Task<QuoteData> GetLatestQuoteAsync(string asset, CancellationToken ct = default);

    bool TryGetCachedQuote(string asset, out QuoteData? quote);

    /// <summary>
    /// Keeps a pair streaming so its candles stay resident.
    ///
    /// <para>Residency is what makes a bar-close scan cost nothing. Fetching on demand
    /// serialises behind the broker's one-subscription-at-a-time behaviour and turns that
    /// scan into a multi-second crawl.</para>
    /// </summary>
    Task SubscribePairAsync(string asset, int periodSeconds = 60, CancellationToken ct = default);

    void EnsureMarketDataWarm(string asset, int periodSeconds = 60);

    bool HasFreshHistory(string asset, int periodSeconds);

    Task<OrderResponse> PlaceOrderAsync(
        string asset,
        TradeDirection direction,
        decimal amount,
        int durationSeconds,
        CancellationToken ct = default);

    Task<TradeOutcome> WaitOutcomeAsync(string orderId, CancellationToken ct = default);

    bool TryGetClosedPnl(string orderId, out decimal profitLoss);

    Task DisconnectAsync(CancellationToken ct = default);
}

/// <summary>
/// Resolves the right <see cref="IBrokerClient"/> for a user.
///
/// <para>A user is linked to exactly one broker at a time (the choice they make at login),
/// so callers ask for "this user's client" and never for a particular venue.</para>
/// </summary>
public interface IBrokerSessionManager
{
    /// <summary>The user's live client, or null when there is no session.</summary>
    IBrokerClient? Get(Guid userId, string broker);

    /// <summary>Opens or reuses a session on the named broker.</summary>
    Task<IBrokerClient> GetOrCreateAsync(
        Guid userId,
        string broker,
        BrokerCredentials credentials,
        CancellationToken ct = default);

    Task RemoveAsync(Guid userId, string broker, CancellationToken ct = default);

    int ActiveSessionCount { get; }
}

/// <summary>
/// What a broker needs to open a session. Which fields matter is the adapter's business:
/// Binolla authenticates on an SSID, Quotex accepts either an SSID or a credential pair.
/// </summary>
/// <param name="Ssid">Session token copied from the broker, when available.</param>
/// <param name="CookieHeader">Cookies captured alongside the SSID, when the broker needs them.</param>
/// <param name="Email">Login email, for brokers that accept credentials directly.</param>
/// <param name="Password">Login password. Never logged, never persisted unencrypted.</param>
/// <param name="AccountType">
/// Which balance the session opens on. Applied during the handshake — switching afterwards
/// races the broker's own setup and can leave the socket on the other balance while the
/// API reports this one.
/// </param>
public sealed record BrokerCredentials(
    string? Ssid = null,
    string? CookieHeader = null,
    string? Email = null,
    string? Password = null,
    AccountType AccountType = AccountType.Real);
