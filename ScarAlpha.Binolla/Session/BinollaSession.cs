using System.Collections.Concurrent;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;
using ScarAlpha.Binolla.Transport;

namespace ScarAlpha.Binolla.Session;

/// <summary>
/// Isolated multi-user Binolla trading session. One instance per user.
/// </summary>
public sealed class BinollaSession : IBinollaClient
{
    private readonly BinollaSessionManagerOptions _options;
    private readonly WebSocketTransportFactory _transportFactory;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _inboundGate = new(1, 1);
    private readonly SemaphoreSlim _subscribeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _warmInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly OrderCorrelationHub _orders = new();
    private int _alertsPrimed;

    private IWebSocketTransport? _trading;
    private IWebSocketTransport? _chart;
    private SessionMessageRouter? _router;
    private CancellationTokenSource? _sessionCts;
    private int _reconnectAttempts;
    private int _disposed;
    private TaskCompletionSource<bool>? _authTcs;
    private TaskCompletionSource<bool>? _balanceTcs;

    public BinollaSession(
        string userId,
        BinollaSessionManagerOptions? options = null,
        WebSocketTransportFactory? transportFactory = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required.", nameof(userId));

        _options = options ?? new BinollaSessionManagerOptions();
        _transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        State = new BinollaSessionState
        {
            UserId = userId,
            ChartEnabled = _options.EnableChartConnection
        };
    }

    public BinollaSessionState State { get; }

    public string UserId => State.UserId;

    public SessionLifecycleState Lifecycle => State.Lifecycle;

    public bool IsTransportConnected => _trading?.IsConnected == true;

    /// <summary>
    /// Wait while another connect/reauth is in flight. Avoids stomping a live handshake.
    /// </summary>
    public async Task WaitUntilNotConnectingAsync(CancellationToken cancellationToken = default)
    {
        while (Lifecycle is SessionLifecycleState.Connecting or SessionLifecycleState.Reconnecting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    public event Action<SessionLifecycleState, string?>? LifecycleChanged;
    public event Action? OnConnectionLost;
    public event Action? OnReconnected;
    public event Action<TradeOutcome>? OnOrderClosed;
    public event Action? OnSessionExpired;

    public async Task ConnectAsync(
        string ssid,
        CancellationToken cancellationToken = default,
        string? cookieHeader = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(ssid))
            throw new BinollaAuthenticationException("SSID is required.");

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
            {
                // Zombie guard: Lifecycle can stay Connected after the socket died.
                if (_trading?.IsConnected == true)
                    return;
            }

            SetLifecycle(SessionLifecycleState.Connecting);
            State.Ssid = ssid;
            // Keep Playwright cookies across SSID-only restores/reconnects.
            // Wiping CookieHeader here caused post-login /api/account/status to hang 20s then fail.
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                State.CookieHeader = cookieHeader.Trim();
            // Session lifetime must NOT link to the HTTP request CT — RequestAborted / FE abort
            // was cancelling the receive loop mid-handshake and producing auth timeouts.
            try { _sessionCts?.Cancel(); } catch { /* ignore */ }
            try { _sessionCts?.Dispose(); } catch { /* ignore */ }
            _sessionCts = new CancellationTokenSource();
            _reconnectAttempts = 0;
            Interlocked.Exchange(ref _alertsPrimed, 0);

            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H80", "BinollaSession.ConnectAsync", "connecting", new
            {
                hasCookie = !string.IsNullOrEmpty(State.CookieHeader),
                cookieLen = State.CookieHeader?.Length ?? 0,
                ssidLen = ssid.Length,
                priorLifecycle = Lifecycle.ToString()
            });
            // #endregion

            var connectSw = System.Diagnostics.Stopwatch.StartNew();
            await ConnectSocketsAsync(_sessionCts.Token).ConfigureAwait(false);
            var socketMs = connectSw.ElapsedMilliseconds;
            await WaitForAuthenticationAsync(cancellationToken).ConfigureAwait(false);

            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H80", "BinollaSession.ConnectAsync", "auth_ok", new
            {
                lifecycle = Lifecycle.ToString(),
                transportUp = _trading?.IsConnected == true,
                socketMs,
                authMs = connectSw.ElapsedMilliseconds
            });
            // #endregion

            // Final guard — unauthorized can race in after auth_ok and leave AuthFailed
            // while ConnectAsync would otherwise return successfully (login then 502 on ChangeAccount).
            if (Lifecycle == SessionLifecycleState.AuthenticationFailed)
                throw new BinollaAuthenticationException("SSID not authorized.");
            if (Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected) ||
                _trading?.IsConnected != true)
            {
                throw new BinollaAuthenticationException(
                    $"Authentication did not complete (state={Lifecycle}).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H80", "BinollaSession.ConnectAsync", "connect_failed", new
            {
                type = ex.GetType().Name,
                message = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message,
                lifecycle = Lifecycle.ToString()
            });
            // #endregion

            SetLifecycle(
                ex is BinollaAuthenticationException
                    ? SessionLifecycleState.AuthenticationFailed
                    : SessionLifecycleState.Faulted,
                SafeError(ex));

            await SafeCloseSocketsAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<BalanceInfo> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        State.Touch();

        if (State.BalanceUpdatedAt is not null)
            return State.GetBalanceInfo();

        // Nudge server for balances if none received yet after auth.
        try
        {
            var transport = _trading;
            if (transport is not null && transport.IsConnected)
                await transport.SendAsync("42[\"balances/list\"]", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }

        // Keep page navigations snappy — Home/Trading poll; do not burn MarketDataTimeout here.
        try
        {
            await WaitForConditionAsync(
                    () => State.BalanceUpdatedAt is not null,
                    TimeSpan.FromSeconds(4),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (BinollaTimeoutException)
        {
            // fall through — soft empty Demo snapshot
        }
        catch (OperationCanceledException)
        {
            // fall through
        }

        if (State.BalanceUpdatedAt is null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H132", "BinollaSession.GetBalanceAsync", "balance_soft_empty", new
            {
                assetsCached = State.Assets.Count
            });
            // #endregion
            return new BalanceInfo
            {
                DemoBalance = 0m,
                RealBalance = 0m,
                CurrentType = AccountType.Demo,
                LastUpdated = null,
                Currency = "USD"
            };
        }

        return State.GetBalanceInfo();
    }

    public async Task ChangeAccountAsync(AccountType accountType, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        State.Touch();

        var transport = _trading ?? throw new BinollaConnectionException("Not connected.");
        await transport.SendAsync(
                BinollaFraming.BuildAccountChange(accountType == AccountType.Demo),
                cancellationToken)
            .ConfigureAwait(false);

        State.SetAccountType(accountType);
    }

    public async Task<OrderResponse> PlaceOrderAsync(
        string asset,
        TradeDirection direction,
        decimal amount,
        int durationSeconds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        State.Touch();

        if (string.IsNullOrWhiteSpace(asset))
            throw new BinollaOrderException("Asset is required.");
        if (amount <= 0)
            throw new BinollaOrderException("Amount must be greater than zero.");
        if (durationSeconds <= 0)
            throw new BinollaOrderException("Duration must be greater than zero.");

        var requestId = _orders.NextRequestId();
        var tcs = _orders.RegisterOpen(requestId, asset, direction, amount, durationSeconds);

        var expiryUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + durationSeconds + 1;
        var cmd = direction == TradeDirection.Call ? 0 : 1;
        var frame = BinollaFraming.BuildOrderOpen(asset, expiryUnix, amount, cmd, requestId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.PlaceOrderTimeout);

        await using var reg = timeoutCts.Token.Register(() =>
        {
            if (!_orders.RemoveOpenWaiter(requestId))
                return;

            if (cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);
            else
                tcs.TrySetException(new BinollaTimeoutException("Place order timed out."));
        });

        try
        {
            var transport = _trading ?? throw new BinollaConnectionException("Not connected.");
            await transport.SendAsync(frame, timeoutCts.Token).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _orders.RemoveOpenWaiter(requestId);
            throw new BinollaTimeoutException("Place order timed out.");
        }
        catch
        {
            _orders.RemoveOpenWaiter(requestId);
            throw;
        }
    }

    public async Task<TradeOutcome> WaitOutcomeAsync(string orderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(orderId))
            throw new BinollaOrderException("Order id is required.");

        State.Touch();

        if (State.ClosedOrderPnL.TryGetValue(orderId, out var existingPnl))
        {
            return new TradeOutcome
            {
                OrderId = orderId,
                ProfitLoss = existingPnl,
                ClosedAt = DateTimeOffset.UtcNow,
                Result = existingPnl > 0 ? TradeResult.Win : existingPnl < 0 ? TradeResult.Loss : TradeResult.Tie
            };
        }

        var tcs = _orders.RegisterOutcome(orderId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.OutcomeTimeout);

        await using var reg = timeoutCts.Token.Register(() =>
        {
            if (!_orders.RemoveOutcomeWaiter(orderId))
                return;

            if (cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);
            else
                tcs.TrySetException(new BinollaTimeoutException("Outcome wait timed out for order."));
        });

        try
        {
            if (State.ClosedOrderPnL.TryGetValue(orderId, out var racePnl))
                _orders.TryCompleteOutcome(orderId, racePnl, null);

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _orders.RemoveOutcomeWaiter(orderId);
            throw new BinollaTimeoutException("Outcome wait timed out.");
        }
        catch (OperationCanceledException)
        {
            _orders.RemoveOutcomeWaiter(orderId);
            throw;
        }
        catch
        {
            if (!tcs.Task.IsCompleted)
                _orders.RemoveOutcomeWaiter(orderId);
            throw;
        }
    }

    public async Task SubscribePairAsync(string pair, int period = 60, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // asset/change during a brief unauthorized reauth — wait for Connected again.
        if (Lifecycle is SessionLifecycleState.Connecting or SessionLifecycleState.Reconnecting)
        {
            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(TimeSpan.FromSeconds(12));
                await WaitUntilNotConnectingAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // fall through to EnsureConnected
            }
        }

        EnsureConnected();
        State.Touch();

        var requestedPeriod = period;
        var wirePeriod = BinollaMarketPeriods.NormalizeHistoryPeriod(period);
        var transport = _trading ?? throw new BinollaConnectionException("Not connected.");
        // Do not let a cancelled HTTP tick abort another request's subscribe — use a short local wait.
        using var lockCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lockCts.CancelAfter(TimeSpan.FromSeconds(8));
        await _subscribeLock.WaitAsync(lockCts.Token).ConfigureAwait(false);
        try
        {
            // Remember before send — soft unauthorized reauth re-nudges SubscribedPairs.
            State.RememberSubscription(pair);
            // Match upstream exactly: alerts before every asset/change (not once per session).
            await transport.SendAsync(BinollaFraming.BuildAlertList(), CancellationToken.None).ConfigureAwait(false);
            await transport.SendAsync(BinollaFraming.BuildAlertClosedList(), CancellationToken.None).ConfigureAwait(false);
            // ONE asset/change only. Sending 14400 after 60 cancels the reliable OTC history push
            // (PM2: history never arrives for EURUSD_otc period=14400 within 30s).
            await transport.SendAsync(BinollaFraming.BuildAssetChange(pair, wirePeriod), CancellationToken.None)
                .ConfigureAwait(false);

            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H113", "BinollaSession.SubscribePairAsync", "asset_change_sent", new
            {
                pair,
                requestedPeriod,
                wirePeriod,
                clamped = requestedPeriod != wirePeriod,
                lifecycle = Lifecycle.ToString(),
                subscribed = State.SubscribedPairs.Count,
                unauthorized = _router?.UnauthorizedSeen ?? 0,
                assetsCached = State.Assets.Count
            });
            // #endregion
        }
        finally
        {
            _subscribeLock.Release();
        }
    }

    public void EnsureMarketDataWarm(string asset, int period = 60)
    {
        if (string.IsNullOrWhiteSpace(asset))
            return;
        if (Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
            return;
        if (!IsTransportConnected)
            return;

        var key = asset.Trim();
        var wirePeriod = BinollaMarketPeriods.NormalizeHistoryPeriod(period);
        var warmKey = $"{key}:{wirePeriod}";
        if (!_warmInFlight.TryAdd(warmKey, 1))
            return;

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H133", "BinollaSession.EnsureMarketDataWarm", "warm_queued", new
        {
            symbol = key,
            wirePeriod
        });
        // #endregion

        _ = Task.Run(async () =>
        {
            try
            {
                await SubscribePairAsync(key, wirePeriod, CancellationToken.None).ConfigureAwait(false);
                var budget = _options.MarketDataTimeout;
                if (budget < TimeSpan.FromSeconds(8))
                    budget = TimeSpan.FromSeconds(20);
                try
                {
                    await WaitForConditionAsync(
                            () => FindHistoryFor(key, wirePeriod) is not null || FindQuoteFor(key) is not null,
                            budget,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (BinollaTimeoutException)
                {
                    // next HTTP poll may still hit a late push
                }

                // #region agent log
                ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H133", "BinollaSession.EnsureMarketDataWarm", "warm_done", new
                {
                    symbol = key,
                    wirePeriod,
                    historyHit = FindHistoryFor(key, wirePeriod) is not null,
                    quoteHit = FindQuoteFor(key) is not null,
                    historyKeys = State.HistoricalData.Keys.Take(8).ToArray(),
                    quoteKeys = State.LatestQuotes.Keys.Take(8).ToArray()
                });
                // #endregion
            }
            catch (Exception)
            {
                // best effort — never surface to HTTP
            }
            finally
            {
                _warmInFlight.TryRemove(warmKey, out _);
            }
        });
    }

    public async Task<IReadOnlyList<TradingAsset>> GetTradingAssetsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        State.Touch();

        if (State.Assets.Count == 0)
        {
            try
            {
                var transport = _trading;
                if (transport is not null && transport.IsConnected)
                    await transport.SendAsync("42[\"assets/list\"]", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best effort nudge
            }

            // First paint after restore often needs >2.5s for s_assets/list binary.
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                await WaitForConditionAsync(
                        () => State.Assets.Count > 0,
                        TimeSpan.FromSeconds(12),
                        waitCts.Token)
                    .ConfigureAwait(false);
            }
            catch (BinollaTimeoutException)
            {
                // Prefer empty list over hanging the UI.
            }
            catch (OperationCanceledException)
            {
                // Prefer empty list over hanging the UI.
            }
        }

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H111", "BinollaSession.GetTradingAssetsAsync", "assets_ready", new
        {
            count = State.Assets.Count,
            timeoutSec = _options.MarketDataTimeout.TotalSeconds
        });
        // #endregion

        var mapped = State.Assets
            .Select(a => new TradingAsset
            {
                Symbol = a.Name,
                Description = a.Description,
                IsOpen = a.IsOpen,
                PayoutPercentage = a.Payout,
                Category = a.Type
            })
            .ToList();

        var open = mapped.Where(a => a.IsOpen).ToList();
        return open.Count > 0 ? open : mapped;
    }

    public async Task<QuoteData> GetLatestQuoteAsync(string asset, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(asset))
            throw new BinollaOrderException("asset is required.");

        State.Touch();
        var key = asset.Trim();

        if (FindQuoteFor(key) is { } existing)
            return existing;

        // Subscribe/wait independent of HTTP abort (same as GetHistoryAsync).
        await SubscribePairAsync(key, 60, CancellationToken.None).ConfigureAwait(false);

        if (FindQuoteFor(key) is { } afterSub)
            return afterSub;

        var httpWait = _options.MarketHttpWait;
        if (httpWait < TimeSpan.FromSeconds(1))
            httpWait = TimeSpan.FromSeconds(4);
        try
        {
            await WaitForConditionAsync(
                    () => FindQuoteFor(key) is not null || FindHistoryFor(key, 60) is not null,
                    httpWait,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (BinollaTimeoutException)
        {
            EnsureMarketDataWarm(key, 60);
        }

        if (FindQuoteFor(key) is { } quote)
            return quote;

        // Synthesize from last candle/tick so price soft-path can return 200 instead of 5xx.
        if (TryQuoteFromHistory(key, out var synthesized) && synthesized is not null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H134", "BinollaSession.GetLatestQuoteAsync", "quote_from_history", new
            {
                symbol = key,
                price = synthesized.Price
            });
            // #endregion
            return synthesized;
        }

        EnsureMarketDataWarm(key, 60);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H112", "BinollaSession.GetLatestQuoteAsync", "quote_timeout", new
        {
            symbol = key,
            assetsCached = State.Assets.Count,
            quotesCached = State.LatestQuotes.Count,
            quoteKeys = State.LatestQuotes.Keys.Take(8).ToArray(),
            httpWaitMs = httpWait.TotalMilliseconds
        });
        // #endregion
        throw new BinollaTimeoutException("Quote not available for asset.");
    }

    public async Task<HistoryData> GetHistoryAsync(string asset, int period, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(asset))
            throw new BinollaOrderException("asset is required.");
        if (period is < 1 or > 86400)
            throw new BinollaOrderException("period must be between 1 and 86400 seconds.");

        State.Touch();
        var key = asset.Trim();
        var requestedPeriod = period;
        var wirePeriod = BinollaMarketPeriods.NormalizeHistoryPeriod(period);

        if (FindHistoryFor(key, wirePeriod) is { } cached)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H115", "BinollaSession.GetHistoryAsync", "history_hit_cache", new
            {
                symbol = key,
                requestedPeriod,
                wirePeriod,
                returnedPeriod = cached.Period,
                candleCount = cached.Candles.Count,
                tickCount = cached.TickHistory.Count
            });
            // #endregion
            return cached;
        }

        await SubscribePairAsync(key, wirePeriod, CancellationToken.None).ConfigureAwait(false);
        if (FindHistoryFor(key, wirePeriod) is { } afterSub)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H115", "BinollaSession.GetHistoryAsync", "history_hit_after_sub", new
            {
                symbol = key,
                requestedPeriod,
                wirePeriod,
                returnedPeriod = afterSub.Period,
                candleCount = afterSub.Candles.Count,
                tickCount = afterSub.TickHistory.Count
            });
            // #endregion
            return afterSub;
        }

        // Short HTTP wait — soft empty + background warm; never block ~30s.
        var httpWait = _options.MarketHttpWait;
        if (httpWait < TimeSpan.FromSeconds(1))
            httpWait = TimeSpan.FromSeconds(4);
        try
        {
            await WaitForConditionAsync(
                    () => FindHistoryFor(key, wirePeriod) is not null,
                    httpWait,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (BinollaTimeoutException)
        {
            // one quick re-nudge with period 60 (OTC baseline), then soft miss
            try
            {
                if (wirePeriod != 60)
                    await SubscribePairAsync(key, 60, CancellationToken.None).ConfigureAwait(false);
                else
                    await SubscribePairAsync(key, wirePeriod, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore nudge failures
            }

            try
            {
                await WaitForConditionAsync(
                        () => FindHistoryFor(key, wirePeriod) is not null,
                        TimeSpan.FromSeconds(1.5),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (BinollaTimeoutException)
            {
                // fall through
            }
        }

        if (FindHistoryFor(key, wirePeriod) is { } history)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H115", "BinollaSession.GetHistoryAsync", "history_hit_after_wait", new
            {
                symbol = key,
                requestedPeriod,
                wirePeriod,
                returnedPeriod = history.Period,
                candleCount = history.Candles.Count,
                tickCount = history.TickHistory.Count
            });
            // #endregion
            return history;
        }

        EnsureMarketDataWarm(key, wirePeriod);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H112", "BinollaSession.GetHistoryAsync", "history_timeout", new
        {
            symbol = key,
            requestedPeriod,
            wirePeriod,
            assetsCached = State.Assets.Count,
            historyCached = State.HistoricalData.Count,
            quotesCached = State.LatestQuotes.Count,
            historyKeys = State.HistoricalData.Keys.Take(8).ToArray(),
            httpWaitMs = httpWait.TotalMilliseconds,
            timeoutSec = _options.MarketDataTimeout.TotalSeconds
        });
        // #endregion
        throw new BinollaTimeoutException("History not available for asset/period.");
    }

    /// <summary>
    /// Prefer exact period, else period 60, else any non-empty history for the asset.
    /// </summary>
    private HistoryData? FindHistoryFor(string key, int preferredPeriod)
    {
        var preferredKey = $"{key}:{preferredPeriod}";
        if (State.HistoricalData.TryGetValue(preferredKey, out var exact) &&
            (exact.Candles.Count > 0 || exact.TickHistory.Count > 0))
            return exact;

        // Prefer period 60 when available (OTC baseline), then any non-empty for the asset.
        HistoryData? any = null;
        HistoryData? sixty = null;
        foreach (var pair in State.HistoricalData)
        {
            if (!pair.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                continue;
            if (pair.Value.Candles.Count == 0 && pair.Value.TickHistory.Count == 0)
                continue;
            any ??= pair.Value;
            if (pair.Value.Period == 60 || pair.Key.EndsWith(":60", StringComparison.Ordinal))
                sixty = pair.Value;
        }

        return sixty ?? any;
    }

    private QuoteData? FindQuoteFor(string key)
    {
        if (State.LatestQuotes.TryGetValue(key, out var exact))
            return exact;
        foreach (var pair in State.LatestQuotes)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private bool TryQuoteFromHistory(string key, out QuoteData? quote)
    {
        quote = null;
        var history = FindHistoryFor(key, 60);
        if (history is null)
            return false;

        if (history.TickHistory.Count > 0)
        {
            var tick = history.TickHistory[^1];
            quote = new QuoteData
            {
                Pair = key,
                Timestamp = tick.Timestamp,
                Price = tick.Price,
                ReceivedAt = DateTimeOffset.UtcNow
            };
            return true;
        }

        if (history.Candles.Count > 0)
        {
            var candle = history.Candles.OrderBy(c => c.Timestamp).Last();
            quote = new QuoteData
            {
                Pair = key,
                Timestamp = candle.Timestamp,
                Price = candle.Close,
                ReceivedAt = DateTimeOffset.UtcNow
            };
            return true;
        }

        return false;
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (condition())
            return;

        // Dedicated timeout CTS — do NOT link caller CT into CancelAfter semantics.
        // Passing an already-CancelAfter token made timeout look like caller cancel
        // (PM2: TaskCanceledException at ~15s → Unable to load candles 500).
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            while (!condition())
            {
                linked.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H130",
                "BinollaSession.WaitForConditionAsync",
                "mapped_timeout",
                new { timeoutMs = timeout.TotalMilliseconds });
            // #endregion
            throw new BinollaTimeoutException("Timed out waiting for market data.");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            return;

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try { _sessionCts?.Cancel(); } catch { /* ignore */ }
            _orders.CancelAllPending();
            await SafeCloseSocketsAsync().ConfigureAwait(false);
            State.Ssid = null;
            SetLifecycle(SessionLifecycleState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ignore dispose failures
        }

        _lifecycleLock.Dispose();
        _inboundGate.Dispose();
        _sessionCts?.Dispose();
    }

    // ── internals ──────────────────────────────────────────────

    private async Task ConnectSocketsAsync(CancellationToken cancellationToken)
    {
        await SafeCloseSocketsAsync().ConfigureAwait(false);

        _trading = _transportFactory();
        _router = new SessionMessageRouter(
            State,
            _orders,
            async (msg, ct) =>
            {
                if (_trading is null || !_trading.IsConnected) return;
                // Never log SSID
                await _trading.SendAsync(msg, ct).ConfigureAwait(false);
            },
            outcome => OnOrderClosed?.Invoke(outcome),
            onAuthorized: () =>
            {
                _authTcs?.TrySetResult(true);
            });

        // Fresh transport each connect — still unsubscribe first so reconnect never double-fires.
        _trading.TextMessageReceived -= OnTradingMessage;
        _trading.Closed -= OnTradingClosed;
        _trading.TextMessageReceived += OnTradingMessage;
        _trading.Closed += OnTradingClosed;

        _authTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _balanceTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tradingUri = _options.TradingSocketUri ?? new Uri(BinollaWire.TradingSocketUri);
        // Cache-buster avoids intermediary reuse of a half-open Engine.IO upgrade.
        var uriBuilder = new UriBuilder(tradingUri);
        var query = string.IsNullOrEmpty(uriBuilder.Query)
            ? "EIO=4&transport=websocket"
            : uriBuilder.Query.TrimStart('?');
        if (!query.Contains("t=", StringComparison.Ordinal))
            query += (query.Length > 0 ? "&" : "") + "t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        uriBuilder.Query = query;
        tradingUri = uriBuilder.Uri;

        var headers = new Dictionary<string, string>
        {
            ["Origin"] = BinollaWire.Origin,
            ["User-Agent"] = BinollaWire.UserAgent
        };
        // Pass Playwright cookies via CookieContainer (not raw Cookie header).
        // Live unauthorized logs showed SSID-only sessions rejected on asset/change.
        if (!string.IsNullOrWhiteSpace(State.CookieHeader))
            headers["Cookie"] = State.CookieHeader;

        await _trading.ConnectAsync(tradingUri, headers, cancellationToken).ConfigureAwait(false);

        if (_options.EnableChartConnection)
        {
            _chart = _transportFactory();
            var chartUri = _options.ChartSocketUri ?? new Uri(BinollaWire.ChartSocketUri);
            var chartHeaders = new Dictionary<string, string>
            {
                ["Host"] = BinollaWire.HostChart,
                ["Origin"] = BinollaWire.Origin,
                ["User-Agent"] = BinollaWire.UserAgent
            };
            await _chart.ConnectAsync(chartUri, chartHeaders, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(State.Ssid))
                await _chart.SendAsync(State.Ssid, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnTradingMessage(string message)
    {
        var router = _router;
        if (router is null) return;

        // Serialize protocol handling per session so 451 header + payload pairs never interleave.
        var ct = _sessionCts?.Token ?? CancellationToken.None;
        _ = ProcessInboundAsync(router, message, ct);
    }

    private async Task ProcessInboundAsync(SessionMessageRouter router, string message, CancellationToken ct)
    {
        await _inboundGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await router.HandleRawAsync(message, ct).ConfigureAwait(false);

            if (State.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
                _authTcs?.TrySetResult(true);

            if (State.Lifecycle == SessionLifecycleState.AuthenticationFailed)
            {
                _authTcs?.TrySetException(new BinollaAuthenticationException("SSID not authorized."));
                OnSessionExpired?.Invoke();
            }

            if (State.BalanceUpdatedAt is not null)
                _balanceTcs?.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            // session shutting down
        }
        catch (Exception ex)
        {
            _authTcs?.TrySetException(ex);
        }
        finally
        {
            _inboundGate.Release();
        }
    }

    private void OnTradingClosed(Exception? error)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            return;

        if (Lifecycle is SessionLifecycleState.Disconnected)
            return;

        // Previously ignored closes during Connecting — auth then hung until timeout with zero frames.
        if (Lifecycle is SessionLifecycleState.Connecting)
        {
            var authFail = new BinollaConnectionException(
                error is null ? "WebSocket closed during authentication." : "WebSocket lost during authentication.");
            _authTcs?.TrySetException(authFail);
            SetLifecycle(SessionLifecycleState.Faulted, SafeError(error));
            return;
        }

        SetLifecycle(SessionLifecycleState.Disconnected, SafeError(error));
        OnConnectionLost?.Invoke();

        var fail = new BinollaConnectionException(
            error is null ? "Connection closed." : "Connection lost.");
        _orders.FailAllPending(fail);
        _authTcs?.TrySetException(fail);

        if (_options.EnableAutoReconnect && !string.IsNullOrEmpty(State.Ssid))
            _ = Task.Run(() => AttemptReconnectAsync());
    }

    private async Task AttemptReconnectAsync()
    {
        if (!_options.EnableAutoReconnect)
            return;

        while (_reconnectAttempts < _options.MaxReconnectAttempts
               && Interlocked.CompareExchange(ref _disposed, 0, 0) == 0)
        {
            _reconnectAttempts++;
            SetLifecycle(SessionLifecycleState.Reconnecting, $"Reconnect attempt {_reconnectAttempts}");

            var delay = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(_reconnectAttempts, 5)));
            try
            {
                await Task.Delay(delay, _sessionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);

                await _lifecycleLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (string.IsNullOrEmpty(State.Ssid))
                    {
                        SetLifecycle(SessionLifecycleState.SessionExpired, "SSID missing; cannot reconnect.");
                        OnSessionExpired?.Invoke();
                        return;
                    }

                    var previousCts = _sessionCts;
                    _sessionCts = new CancellationTokenSource();
                    try { previousCts?.Cancel(); } catch { /* ignore */ }
                    previousCts?.Dispose();

                    await ConnectSocketsAsync(_sessionCts.Token).ConfigureAwait(false);
                    await WaitForAuthenticationAsync(_sessionCts.Token).ConfigureAwait(false);
                    _reconnectAttempts = 0;
                    OnReconnected?.Invoke();
                    return;
                }
                finally
                {
                    _lifecycleLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // try again
            }
        }

        SetLifecycle(SessionLifecycleState.Faulted, "Max reconnect attempts exceeded.");
        _orders.FailAllPending(new BinollaConnectionException("Unable to reconnect."));
    }

    private async Task WaitForAuthenticationAsync(CancellationToken cancellationToken)
    {
        var tcs = _authTcs ?? throw new InvalidOperationException("Auth waiter missing.");

        // Do NOT link to the caller/HTTP/restore CT. Lazy restore used an 18s CTS that cancelled
        // auth mid-handshake while a parallel status restore later succeeded (PM2 assets 18s
        // BINOLLA_NOT_CONNECTED, then "Session restore: connected").
        using var timeoutCts = new CancellationTokenSource(_options.AuthenticationTimeout);

        await using var reg = timeoutCts.Token.Register(() =>
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H101", "BinollaSession.WaitForAuthenticationAsync", "auth_timeout", new
            {
                lifecycle = State.Lifecycle.ToString(),
                timeoutSec = _options.AuthenticationTimeout.TotalSeconds,
                transportUp = _trading?.IsConnected == true,
                callerCanceled = cancellationToken.IsCancellationRequested,
                nsConnects = _router?.NsConnectSends ?? 0,
                unauthorized = _router?.UnauthorizedSeen ?? 0,
                authSignals = _router?.AuthSignals ?? 0,
                lastEvent = _router?.LastInboundEvent
            });
            // #endregion
            tcs.TrySetException(new BinollaTimeoutException("Authentication timed out."));
        });

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BinollaTimeoutException("Authentication timed out.");
        }

        if (State.Lifecycle == SessionLifecycleState.AuthenticationFailed)
            throw new BinollaAuthenticationException("SSID not authorized.");

        if (State.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
            throw new BinollaAuthenticationException("Authentication did not complete.");
    }

    private async Task WaitForBalanceHintAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            if (_balanceTcs is not null)
                await _balanceTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // balance may arrive later; GetBalanceAsync waits properly
        }
    }

    private async Task SafeCloseSocketsAsync()
    {
        if (_trading is not null)
        {
            _trading.TextMessageReceived -= OnTradingMessage;
            _trading.Closed -= OnTradingClosed;
            try { await _trading.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _trading = null;
        }

        if (_chart is not null)
        {
            try { await _chart.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _chart = null;
        }

        _router = null;
    }

    private void SetLifecycle(SessionLifecycleState state, string? error = null)
    {
        State.SetLifecycle(state, error);
        LifecycleChanged?.Invoke(state, error);
    }

    private void EnsureConnected()
    {
        if (Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
            throw new BinollaConnectionException($"Session is not connected (state={Lifecycle}).");
    }

    private void ThrowIfDisposed()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            throw new ObjectDisposedException(nameof(BinollaSession));
    }

    private static string SafeError(Exception? ex)
    {
        if (ex is null) return "Unknown connection error.";
        // Never include potential token material from messages
        return ex.GetType().Name + ": connection or operation failed.";
    }

    // Test hooks
    internal OrderCorrelationHub Orders => _orders;
    internal IWebSocketTransport? TradingTransport => _trading;
}
