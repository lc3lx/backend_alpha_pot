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
                await transport.SendAsync("42[\"balances/list\"]", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }

        await WaitForConditionAsync(
                () => State.BalanceUpdatedAt is not null,
                _options.MarketDataTimeout,
                cancellationToken)
            .ConfigureAwait(false);

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
        await _subscribeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only asset/change — alert/list on every quote tick flooded the socket and
            // interleaved with binary history/quote attachments.
            await transport.SendAsync(BinollaFraming.BuildAssetChange(pair, period), cancellationToken).ConfigureAwait(false);
            State.RememberSubscription(pair);
        }
        finally
        {
            _subscribeLock.Release();
        }
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
                    await transport.SendAsync("42[\"assets/list\"]", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort nudge
            }

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
        await SubscribePairAsync(key, 60, cancellationToken).ConfigureAwait(false);

        if (State.LatestQuotes.TryGetValue(key, out var existing))
            return existing;

        var half = TimeSpan.FromMilliseconds(Math.Max(500, _options.MarketDataTimeout.TotalMilliseconds / 2));
        try
        {
            await WaitForConditionAsync(
                    () => State.LatestQuotes.ContainsKey(key),
                    half,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BinollaTimeoutException)
        {
            try
            {
                await SubscribePairAsync(key, 60, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            await WaitForConditionAsync(
                    () => State.LatestQuotes.ContainsKey(key),
                    half,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!State.LatestQuotes.TryGetValue(key, out var quote))
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H112", "BinollaSession.GetLatestQuoteAsync", "quote_timeout", new
            {
                symbol = key,
                assetsCached = State.Assets.Count,
                quotesCached = State.LatestQuotes.Count
            });
            // #endregion
            throw new BinollaTimeoutException("Quote not available for asset.");
        }

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
            return existing;

        // Mid-wait re-subscribe — first asset/change after login is often ignored until
        // assets/list has landed; a second nudge recovers history within MarketDataTimeout.
        var half = TimeSpan.FromMilliseconds(Math.Max(500, _options.MarketDataTimeout.TotalMilliseconds / 2));
        try
        {
            await WaitForConditionAsync(
                    () => State.HistoricalData.ContainsKey(historyKey),
                    half,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BinollaTimeoutException)
        {
            try
            {
                await SubscribePairAsync(key, period, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore nudge failures
            }

            await WaitForConditionAsync(
                    () => State.HistoricalData.ContainsKey(historyKey),
                    half,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!State.HistoricalData.TryGetValue(historyKey, out var history))
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H112", "BinollaSession.GetHistoryAsync", "history_timeout", new
            {
                symbol = key,
                period,
                assetsCached = State.Assets.Count,
                historyCached = State.HistoricalData.Count,
                quotesCached = State.LatestQuotes.Count,
                timeoutSec = _options.MarketDataTimeout.TotalSeconds
            });
            // #endregion
            throw new BinollaTimeoutException("History not available for asset/period.");
        }

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
