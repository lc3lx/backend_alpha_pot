using System.Collections.Concurrent;
using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Binolla.Session;

/// <summary>
/// Per-session mutable state. Never shared across users.
/// Replaces process-wide Globals from upstream.
/// </summary>
public sealed class BinollaSessionState
{
    private readonly object _balanceGate = new();
    private readonly object _assetsGate = new();
    private readonly object _lifecycleGate = new();

    public required string UserId { get; init; }

    /// <summary>SSID stored in memory only for connection use. Never log.</summary>
    public string? Ssid { get; set; }

    /// <summary>Optional Cookie header from Playwright capture. Never log.</summary>
    public string? CookieHeader { get; set; }

    public SessionLifecycleState Lifecycle
    {
        get { lock (_lifecycleGate) return _lifecycle; }
        private set { lock (_lifecycleGate) _lifecycle = value; }
    }

    private SessionLifecycleState _lifecycle = SessionLifecycleState.Disconnected;

    public string? LastError { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;
    public bool ChartEnabled { get; set; }

    public AccountType AccountType { get; private set; } = AccountType.Demo;
    public decimal RealBalance { get; private set; }
    public decimal DemoBalance { get; private set; }
    public DateTimeOffset? BalanceUpdatedAt { get; private set; }

    public const int MaxHistoricalEntries = 24;

    public ConcurrentDictionary<string, QuoteData> LatestQuotes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, HistoryData> HistoricalData { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, decimal> ClosedOrderPnL { get; } = new(StringComparer.OrdinalIgnoreCase);

    private List<AssetDataWire> _assets = new();
    private HashSet<string> _subscribedPairs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AssetDataWire> Assets
    {
        get { lock (_assetsGate) return _assets.ToList(); }
    }

    public IReadOnlyCollection<string> SubscribedPairs
    {
        get { lock (_assetsGate) return _subscribedPairs.ToList(); }
    }

    public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;

    public void SetLifecycle(SessionLifecycleState state, string? error = null)
    {
        Lifecycle = state;
        if (error is not null)
            LastError = error;

        if (state is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
            ConnectedAt = DateTimeOffset.UtcNow;

        Touch();
    }

    public void SetAccountType(AccountType type)
    {
        AccountType = type;
        Touch();
    }

    public void UpdateBalance(decimal? demo, decimal? real)
    {
        lock (_balanceGate)
        {
            if (demo.HasValue) DemoBalance = demo.Value;
            if (real.HasValue) RealBalance = real.Value;
            BalanceUpdatedAt = DateTimeOffset.UtcNow;
        }

        Touch();
    }

    public void UpdateSingleBalance(bool isDemo, decimal balance)
    {
        lock (_balanceGate)
        {
            if (isDemo) DemoBalance = balance;
            else RealBalance = balance;
            BalanceUpdatedAt = DateTimeOffset.UtcNow;
        }

        Touch();
    }

    public BalanceInfo GetBalanceInfo()
    {
        lock (_balanceGate)
        {
            return new BalanceInfo
            {
                DemoBalance = DemoBalance,
                RealBalance = RealBalance,
                CurrentType = AccountType,
                LastUpdated = BalanceUpdatedAt,
                Currency = "USD"
            };
        }
    }

    public void ReplaceAssets(IEnumerable<AssetDataWire> assets)
    {
        lock (_assetsGate)
        {
            _assets = assets.ToList();
        }

        Touch();
    }

    public void RememberSubscription(string pair)
    {
        lock (_assetsGate)
        {
            _subscribedPairs.Add(pair);
        }

        Touch();
    }

    public void ClearSubscriptions()
    {
        lock (_assetsGate)
        {
            _subscribedPairs.Clear();
        }
    }

    public void ResetMarketCaches()
    {
        LatestQuotes.Clear();
        HistoricalData.Clear();
        lock (_assetsGate)
        {
            _assets = new List<AssetDataWire>();
        }
    }

    /// <summary>
    /// Store history and drop the least-recently-used entries so RAM stays bounded
    /// while the worker rotates a small scan batch.
    /// </summary>
    public void SetHistory(string key, HistoryData history)
    {
        history.AccessedAt = DateTimeOffset.UtcNow;
        HistoricalData[key] = history;
        TrimHistoryLru(key);
    }

    private void TrimHistoryLru(string keepKey)
    {
        var extra = HistoricalData.Count - MaxHistoricalEntries;
        if (extra <= 0)
            return;

        var victims = HistoricalData
            .Where(p => !p.Key.Equals(keepKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Value.AccessedAt)
            .ThenBy(p => p.Value.ReceivedAt)
            .Take(extra)
            .Select(p => p.Key)
            .ToList();
        foreach (var victim in victims)
            HistoricalData.TryRemove(victim, out _);
    }
}
