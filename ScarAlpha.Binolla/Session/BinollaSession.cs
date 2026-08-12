using System.Collections.Concurrent;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Diagnostics;
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
    private readonly OrderCorrelationHub _orders = new();

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
                return;

            SetLifecycle(SessionLifecycleState.Connecting);
            State.Ssid = ssid;
            State.CookieHeader = string.IsNullOrWhiteSpace(cookieHeader) ? null : cookieHeader.Trim();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _reconnectAttempts = 0;

            ProtocolTrace.Write("H12", "BinollaSession.ConnectAsync", "connecting", new
            {
                hasCookie = !string.IsNullOrEmpty(State.CookieHeader),
                cookieLen = State.CookieHeader?.Length ?? 0,
                ssidLen = ssid.Length
            });

            await ConnectSocketsAsync(_sessionCts.Token).ConfigureAwait(false);
            await WaitForAuthenticationAsync(cancellationToken).ConfigureAwait(false);

            // Balance list is bootstrap-driven; wait briefly for first balance event if possible
            await WaitForBalanceHintAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProtocolTrace.Write("H12", "BinollaSession.ConnectAsync", "connect_failed", new
            {
                type = ex.GetType().Name,
                message = ex.Message
            });
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
                await transport.SendAsync("42[\"balances/list\"]", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _balanceTcs, tcs);

        if (State.BalanceUpdatedAt is not null)
            tcs.TrySetResult(true);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.MarketDataTimeout);

            await using var reg = timeoutCts.Token.Register(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetCanceled(cancellationToken);
                else
                    tcs.TrySetException(new BinollaTimeoutException("Balance wait timed out."));
            });

            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BinollaTimeoutException("Balance wait timed out.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _balanceTcs, null, tcs);
        }

        if (State.BalanceUpdatedAt is null)
            throw new BinollaTimeoutException("Balance was not received in time.");

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
        EnsureConnected();
        State.Touch();

        var transport = _trading ?? throw new BinollaConnectionException("Not connected.");
        // #region agent log
        ProtocolTrace.Write("H18", "BinollaSession.SubscribePairAsync", "subscribe_send", new
        {
            pair,
            period,
            quoteCached = State.LatestQuotes.ContainsKey(pair.Trim()),
            historyCached = State.HistoricalData.ContainsKey($"{pair.Trim()}:{period}"),
            quoteCount = State.LatestQuotes.Count,
            historyCount = State.HistoricalData.Count
        });
        // #endregion
        await transport.SendAsync(BinollaFraming.BuildAlertList(), cancellationToken).ConfigureAwait(false);
        await transport.SendAsync(BinollaFraming.BuildAlertClosedList(), cancellationToken).ConfigureAwait(false);
        await transport.SendAsync(BinollaFraming.BuildAssetChange(pair, period), cancellationToken).ConfigureAwait(false);
        State.RememberSubscription(pair);
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
                await WaitForConditionAsync(
                        () => State.Assets.Count > 0,
                        _options.MarketDataTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BinollaTimeoutException)
            {
                // Prefer empty list over hanging the Home/Trading UI.
            }
        }

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
        await SubscribePairAsync(key, 60, cancellationToken).ConfigureAwait(false);

        if (State.LatestQuotes.TryGetValue(key, out var existing))
        {
            // #region agent log
            ProtocolTrace.Write("H18", "BinollaSession.GetLatestQuoteAsync", "quote_cache_hit", new { key, price = existing.Price });
            // #endregion
            return existing;
        }

        try
        {
            await WaitForConditionAsync(
                    () => State.LatestQuotes.ContainsKey(key),
                    _options.MarketDataTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // #region agent log
            ProtocolTrace.Write("H19", "BinollaSession.GetLatestQuoteAsync", "quote_wait_fail", new
            {
                key,
                exType = ex.GetType().Name,
                clientAbort = cancellationToken.IsCancellationRequested,
                quoteKeys = State.LatestQuotes.Keys.Take(8).ToArray(),
                quoteCount = State.LatestQuotes.Count
            });
            // #endregion
            throw;
        }

        if (!State.LatestQuotes.TryGetValue(key, out var quote))
            throw new BinollaTimeoutException("Quote not available for asset.");

        // #region agent log
        ProtocolTrace.Write("H18", "BinollaSession.GetLatestQuoteAsync", "quote_ok", new { key, price = quote.Price });
        // #endregion
        return quote;
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
        var historyKey = $"{key}:{period}";
        await SubscribePairAsync(key, period, cancellationToken).ConfigureAwait(false);

        if (State.HistoricalData.TryGetValue(historyKey, out var existing))
        {
            // #region agent log
            ProtocolTrace.Write("H20", "BinollaSession.GetHistoryAsync", "history_cache_hit", new
            {
                historyKey,
                candles = existing.Candles.Count
            });
            // #endregion
            return existing;
        }

        try
        {
            await WaitForConditionAsync(
                    () => State.HistoricalData.ContainsKey(historyKey),
                    _options.MarketDataTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // #region agent log
            ProtocolTrace.Write("H20", "BinollaSession.GetHistoryAsync", "history_wait_fail", new
            {
                historyKey,
                period,
                exType = ex.GetType().Name,
                clientAbort = cancellationToken.IsCancellationRequested,
                historyKeys = State.HistoricalData.Keys.Take(8).ToArray(),
                historyCount = State.HistoricalData.Count
            });
            // #endregion
            throw;
        }

        if (!State.HistoricalData.TryGetValue(historyKey, out var history))
            throw new BinollaTimeoutException("History not available for asset/period.");

        // #region agent log
        ProtocolTrace.Write("H20", "BinollaSession.GetHistoryAsync", "history_ok", new
        {
            historyKey,
            candles = history.Candles.Count
        });
        // #endregion
        return history;
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (condition())
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            while (!condition())
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
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
                ProtocolTrace.Write("H16", "BinollaSession", "auth_signal_early");
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
        // Intentionally do NOT attach Playwright Cookie header to the trading WS.
        // Live evidence: with Cookie attached, auth waited with zero Engine.IO frames;
        // SSID authorization frame is the auth credential for ws3.

        ProtocolTrace.Write("H14", "BinollaSession.ConnectSocketsAsync", "ws_connect_start", new
        {
            host = tradingUri.Host,
            hasCookie = false,
            cookieCaptured = !string.IsNullOrWhiteSpace(State.CookieHeader),
            queryHasT = tradingUri.Query.Contains("t=", StringComparison.Ordinal)
        });

        await _trading.ConnectAsync(tradingUri, headers, cancellationToken).ConfigureAwait(false);

        ProtocolTrace.Write("H14", "BinollaSession.ConnectSocketsAsync", "ws_tcp_connected");

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
            ProtocolTrace.Write("H17", "BinollaSession.OnTradingClosed", "closed_during_auth", new
            {
                err = error?.GetType().Name
            });
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

        ProtocolTrace.Write("H16", "BinollaSession.WaitForAuthenticationAsync", "auth_wait_start", new
        {
            timeoutSec = _options.AuthenticationTimeout.TotalSeconds,
            lifecycle = State.Lifecycle.ToString()
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.AuthenticationTimeout);

        await using var reg = timeoutCts.Token.Register(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);
            else
                tcs.TrySetException(new BinollaTimeoutException("Authentication timed out."));
        });

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ProtocolTrace.Write("H16", "BinollaSession.WaitForAuthenticationAsync", "auth_wait_timeout", new
            {
                lifecycle = State.Lifecycle.ToString()
            });
            throw new BinollaTimeoutException("Authentication timed out.");
        }

        ProtocolTrace.Write("H16", "BinollaSession.WaitForAuthenticationAsync", "auth_wait_done", new
        {
            lifecycle = State.Lifecycle.ToString()
        });

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
