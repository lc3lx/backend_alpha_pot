using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Infrastructure.BrokerGateway;

/// <summary>
/// Presents the existing Binolla client as an <see cref="IBrokerClient"/>.
///
/// <para>Pure delegation on purpose. Binolla is live with real users and carries fixes that
/// were expensive to find — the official-close overwrite, candle residency, balance
/// selection inside the post-auth bootstrap. None of that is touched here: this adapter
/// only changes the shape callers see, so the venue can be swapped without the working
/// implementation being rewritten underneath it.</para>
/// </summary>
public sealed class BinollaBrokerAdapter : IBrokerClient
{
    private readonly IBinollaClient _inner;

    public BinollaBrokerAdapter(IBinollaClient inner) => _inner = inner;

    public string Broker => Brokers.Binolla;

    public string UserId => _inner.UserId;

    public SessionLifecycleState Lifecycle => _inner.Lifecycle;

    public bool IsTransportConnected => _inner.IsTransportConnected;

    public Task<BalanceInfo> GetBalanceAsync(CancellationToken ct = default) =>
        _inner.GetBalanceAsync(ct);

    public Task ChangeAccountAsync(AccountType accountType, CancellationToken ct = default) =>
        _inner.ChangeAccountAsync(accountType, ct);

    public Task<IReadOnlyList<TradingAsset>> GetTradingAssetsAsync(CancellationToken ct = default) =>
        _inner.GetTradingAssetsAsync(ct);

    public Task<HistoryData> GetHistoryAsync(string asset, int periodSeconds, CancellationToken ct = default) =>
        _inner.GetHistoryAsync(asset, periodSeconds, ct);

    public Task<QuoteData> GetLatestQuoteAsync(string asset, CancellationToken ct = default) =>
        _inner.GetLatestQuoteAsync(asset, ct);

    public bool TryGetCachedQuote(string asset, out QuoteData? quote) =>
        _inner.TryGetCachedQuote(asset, out quote);

    public Task SubscribePairAsync(string asset, int periodSeconds = 60, CancellationToken ct = default) =>
        _inner.SubscribePairAsync(asset, periodSeconds, ct);

    public void EnsureMarketDataWarm(string asset, int periodSeconds = 60) =>
        _inner.EnsureMarketDataWarm(asset, periodSeconds);

    public bool HasFreshHistory(string asset, int periodSeconds) =>
        _inner.HasFreshHistory(asset, periodSeconds);

    public Task<OrderResponse> PlaceOrderAsync(
        string asset,
        TradeDirection direction,
        decimal amount,
        int durationSeconds,
        CancellationToken ct = default) =>
        _inner.PlaceOrderAsync(asset, direction, amount, durationSeconds, ct);

    public Task<TradeOutcome> WaitOutcomeAsync(string orderId, CancellationToken ct = default) =>
        _inner.WaitOutcomeAsync(orderId, ct);

    public bool TryGetClosedPnl(string orderId, out decimal profitLoss) =>
        _inner.TryGetClosedPnl(orderId, out profitLoss);

    public Task DisconnectAsync(CancellationToken ct = default) =>
        _inner.DisconnectAsync(ct);

    /// <summary>The wrapped client, for the few places that still need Binolla directly.</summary>
    internal IBinollaClient Inner => _inner;
}
