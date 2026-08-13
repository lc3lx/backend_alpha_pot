using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScarAlpha.Binolla.Diagnostics;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;

namespace ScarAlpha.Binolla.Session;

/// <summary>
/// Parses Socket.IO/Engine.IO frames and binary event payloads.
/// Logic preserved from ControlMessageProcessor + MessageProcessor, state writes go to session.
/// </summary>
internal sealed class SessionMessageRouter
{
    private readonly BinollaSessionState _state;
    private readonly OrderCorrelationHub _orders;
    private readonly Func<string, CancellationToken, Task> _sendAsync;
    private readonly Action<TradeOutcome>? _onOrderClosed;
    private readonly Action? _onAuthorized;
    private string _upcomingMessageType = string.Empty;
    private int _authorized;
    private int _everAuthorized;
    private int _unauthorizedReauthCount;
    private int _nsConnectSends;
        private int _unauthorizedSeen;
    private int _authSignals;
    private long _lastSoftReauthTicks;
    private string? _lastInboundEvent;
    private const int MaxUnauthorizedReauths = 3;

    public int NsConnectSends => Volatile.Read(ref _nsConnectSends);
    public int UnauthorizedSeen => Volatile.Read(ref _unauthorizedSeen);
    public int AuthSignals => Volatile.Read(ref _authSignals);
    public string? LastInboundEvent => _lastInboundEvent;

    public SessionMessageRouter(
        BinollaSessionState state,
        OrderCorrelationHub orders,
        Func<string, CancellationToken, Task> sendAsync,
        Action<TradeOutcome>? onOrderClosed = null,
        Action? onAuthorized = null)
    {
        _state = state;
        _orders = orders;
        _sendAsync = sendAsync;
        _onOrderClosed = onOrderClosed;
        _onAuthorized = onAuthorized;
    }

    public async Task HandleRawAsync(string message, CancellationToken cancellationToken)
    {
        // Engine.IO OPEN — do not require "sid" substring (packet may vary).
        if (message.StartsWith('0'))
        {
            await _sendAsync("40", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Namespace connected — send auth SSID.
        // Some servers send "40" only; others send 40{"sid":"..."}.
        if (IsSocketIoNamespaceConnect(message))
        {
            var ssid = _state.Ssid
                       ?? throw new BinollaAuthenticationException("SSID is missing.");
            Interlocked.Increment(ref _nsConnectSends);
            _lastInboundEvent = "ns_connect";
            // #region agent log
            LoginTrace.Write("H102", "SessionMessageRouter.HandleRaw", "ns_connect_send_ssid", new
            {
                lifecycle = _state.Lifecycle.ToString(),
                ssidLen = ssid.Length
            });
            // #endregion
            await _sendAsync(ssid, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message == "2")
        {
            await _sendAsync("3", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.StartsWith("451-[", StringComparison.Ordinal))
        {
            HandleBinaryHeader(message);

            // Auth success payload — do not bootstrap on the 451 header alone (that raced
            // bootstrap commands and produced live 42["unauthorized"] storms).
            // Authorization is completed when the binary attachment arrives.

            return;
        }

        // Complete Socket.IO text events: 42["event", payload]
        if (message.StartsWith("42", StringComparison.Ordinal))
        {
            if (IsUnauthorizedMessage(message) ||
                (TryParseSocketIoEvent(message, out var unauthName, out _) &&
                 IsUnauthorizedEventName(unauthName)))
            {
                await HandleUnauthorizedAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // #region agent log
            if (_state.Lifecycle is SessionLifecycleState.Connecting or SessionLifecycleState.Reconnecting &&
                TryParseSocketIoEvent(message, out var inboundName, out _))
            {
                LoginTrace.Write("H102", "SessionMessageRouter.HandleRaw", "connect_event", new
                {
                    eventName = inboundName.Length > 40 ? inboundName[..40] : inboundName,
                    lifecycle = _state.Lifecycle.ToString()
                });
            }
            // #endregion

            if (TryParseSocketIoEvent(message, out var eventName, out var payload))
            {
                if (string.Equals(eventName, BinollaWire.EvAuthorization, StringComparison.Ordinal) ||
                    IsPostAuthSignal(eventName))
                {
                    await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
                }

                await ProcessEventPayloadAsync(eventName, payload).ConfigureAwait(false);
                return;
            }

            // Auth-only text frames without a parseable payload array.
            if (message.Contains(BinollaWire.EvAuthorization, StringComparison.Ordinal) ||
                IsPostAuthSignal(message))
            {
                await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Binary payload following 451-[type] (WS binary decoded to UTF-8).
        // Never treat Engine.IO / Socket.IO control or event frames as attachment payloads —
        // that previously corrupted balances/quotes/history waits.
        if (!string.IsNullOrEmpty(_upcomingMessageType))
        {
            if (LooksLikeEngineIoPacket(message))
            {
                // #region agent log
                LoginTrace.Write("H126", "SessionMessageRouter.HandleRaw", "binary_skipped_eio", new
                {
                    upcoming = _upcomingMessageType,
                    prefix = message.Length > 12 ? message[..12] : message
                });
                // #endregion
            }
            else
            {
                var type = _upcomingMessageType;
                _upcomingMessageType = string.Empty;
                if (string.Equals(type, BinollaWire.EvAuthorization, StringComparison.Ordinal) ||
                    IsPostAuthSignal(type))
                {
                    await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
                }

                await ProcessEventPayloadAsync(type, message).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleUnauthorizedAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _unauthorizedSeen);
        _lastInboundEvent = "unauthorized";
        var lifecycle = _state.Lifecycle;
        var everAuth = Volatile.Read(ref _everAuthorized) == 1;
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H83", "SessionMessageRouter.HandleUnauthorized", "unauthorized_received", new
        {
            lifecycle = lifecycle.ToString(),
            hadAuth = Volatile.Read(ref _authorized) == 1,
            everAuth,
            reauthCount = Volatile.Read(ref _unauthorizedReauthCount)
        });
        // #endregion

        // After the first successful auth, transient unauthorized frames are normal around
        // asset/change. Clearing _authorized forced EnsureAuthorized → full bootstrap again,
        // which stormed unauthorized and starved s_history/last / s_quotes/list (PM2: assets=103
        // OK, candles/price MARKET_UNAVAILABLE after exactly MarketDataTimeout).
        if (everAuth && !string.IsNullOrEmpty(_state.Ssid))
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastSoftReauthTicks);
            if (now - last < 1_500)
            {
                // #region agent log
                LoginTrace.Write("H109", "SessionMessageRouter.HandleUnauthorized", "soft_reauth_throttled", new
                {
                    lifecycle = lifecycle.ToString(),
                    subscribed = _state.SubscribedPairs.Count
                });
                // #endregion
                return;
            }

            Interlocked.Exchange(ref _lastSoftReauthTicks, now);
            // Keep Connected + _authorized=1 so we do NOT re-run PostAuthBootstrap.
            LoginTrace.Write("H109", "SessionMessageRouter.HandleUnauthorized", "soft_reauth_ssid_only", new
            {
                fromLifecycle = lifecycle.ToString(),
                subscribed = _state.SubscribedPairs.Count,
                historyCached = _state.HistoricalData.Count,
                quotesCached = _state.LatestQuotes.Count
            });
            await _sendAsync(_state.Ssid, cancellationToken).ConfigureAwait(false);
            foreach (var pair in _state.SubscribedPairs)
            {
                await _sendAsync(BinollaFraming.BuildAssetChange(pair, 60), cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Pre-auth only: limited SSID retries before AuthenticationFailed.
        if (Volatile.Read(ref _unauthorizedReauthCount) < MaxUnauthorizedReauths &&
            !string.IsNullOrEmpty(_state.Ssid))
        {
            Interlocked.Increment(ref _unauthorizedReauthCount);
            Interlocked.Exchange(ref _authorized, 0);
            _state.ResetMarketCaches();
            _state.ClearSubscriptions();
            if (lifecycle is not (SessionLifecycleState.Connecting or SessionLifecycleState.Reconnecting))
                _state.SetLifecycle(SessionLifecycleState.Connecting, "Binolla unauthorized; re-sending SSID.");

            LoginTrace.Write("H83", "SessionMessageRouter.HandleUnauthorized", "unauthorized_reauth_send", new
            {
                ssidLen = _state.Ssid!.Length,
                fromLifecycle = lifecycle.ToString(),
                reauthCount = Volatile.Read(ref _unauthorizedReauthCount),
                everAuth = false
            });
            await _sendAsync(_state.Ssid, cancellationToken).ConfigureAwait(false);
            return;
        }

        // #region agent log
        LoginTrace.Write("H108", "SessionMessageRouter.HandleUnauthorized", "unauthorized_auth_failed", new
        {
            lifecycle = lifecycle.ToString(),
            reauthCount = Volatile.Read(ref _unauthorizedReauthCount),
            hadAuth = Volatile.Read(ref _authorized) == 1,
            everAuthorized = false
        });
        // #endregion

        _state.ResetMarketCaches();
        _state.ClearSubscriptions();
        _state.SetLifecycle(
            SessionLifecycleState.AuthenticationFailed,
            "Binolla unauthorized.");
        Interlocked.Exchange(ref _authorized, 0);
    }

    private static bool IsUnauthorizedMessage(string message) =>
        message.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ||
        // Exact-ish Socket.IO unauthorized event — avoid matching unrelated payloads.
        message.Contains("\"unauthorized\"", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnauthorizedEventName(string? eventName) =>
        !string.IsNullOrEmpty(eventName) &&
        (eventName.Equals("unauthorized", StringComparison.OrdinalIgnoreCase) ||
         eventName.Equals("NotAuthorized", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeEngineIoPacket(string message)
    {
        if (string.IsNullOrEmpty(message)) return true;
        if (message is "2" or "3") return true;
        // Engine.IO OPEN packet "0{...}" — not a bare '0' digit inside JSON.
        if (message.StartsWith('0') && (message.Length == 1 || message[1] == '{'))
            return true;
        // Precise Socket.IO / Engine.IO message prefixes only.
        // Previously StartsWith('4') skipped binary JSON payloads that began with '4'
        // (e.g. corrupted/partial frames) and left s_history/last unprocessed.
        if (message.StartsWith("40", StringComparison.Ordinal)) return true;
        if (message.StartsWith("41", StringComparison.Ordinal)) return true;
        if (message.StartsWith("42", StringComparison.Ordinal)) return true;
        if (message.StartsWith("43", StringComparison.Ordinal)) return true;
        if (message.StartsWith("44", StringComparison.Ordinal)) return true;
        if (message.StartsWith("45", StringComparison.Ordinal)) return true; // 451 binary header
        return false;
    }

    /// <summary>Parse <c>42["eventName", payload]</c> into name + JSON payload text.</summary>
    internal static bool TryParseSocketIoEvent(string message, out string eventName, out string payload)
    {
        eventName = string.Empty;
        payload = string.Empty;
        if (!message.StartsWith("42[", StringComparison.Ordinal))
            return false;

        try
        {
            var arr = JsonConvert.DeserializeObject<JArray>(message[2..]);
            if (arr is null || arr.Count < 1)
                return false;

            eventName = arr[0]?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(eventName))
                return false;

            if (arr.Count == 1)
            {
                payload = "{}";
                return true;
            }

            var token = arr[1];
            payload = token is null || token.Type == JTokenType.Null
                ? "null"
                : token.Type is JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean
                    ? token.ToString(Formatting.None)
                    : token.ToString(Formatting.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _authSignals);
        _lastInboundEvent = "auth_signal";
        Interlocked.Exchange(ref _everAuthorized, 1);
        if (Interlocked.Exchange(ref _authorized, 1) == 1)
            return;

        // Do NOT reset unauthorizedReauthCount here — that re-enabled infinite
        // unauthorized → reauth → bootstrap loops in production.

        // Mark Connected + unblock WaitForAuthentication BEFORE bootstrap sends.
        _state.SetLifecycle(
            _state.Lifecycle == SessionLifecycleState.Reconnecting
                ? SessionLifecycleState.Reconnected
                : SessionLifecycleState.Connected);
        _state.SetAccountType(AccountType.Demo);
        try { _onAuthorized?.Invoke(); } catch { /* never break protocol */ }

        await SendPostAuthBootstrapAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPostAuthSignal(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        // Only balance/assets/authorization prove the session is usable for market data.
        // s_orders alone previously triggered early bootstrap and unauthorized storms.
        return value.Contains("s_balances/", StringComparison.Ordinal)
               || value.Contains("s_assets/", StringComparison.Ordinal)
               || value.Contains("s_account/", StringComparison.Ordinal)
               || value.Contains(BinollaWire.EvAuthorization, StringComparison.Ordinal);
    }

    private async Task SendPostAuthBootstrapAsync(CancellationToken cancellationToken)
    {
        // Minimal bootstrap first — full list spam after auth often triggers a second unauthorized
        // before the session is stable.
        foreach (var command in BinollaWire.PostAuthBootstrapCommandsEssential)
        {
            await _sendAsync(command, cancellationToken).ConfigureAwait(false);
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        foreach (var command in BinollaWire.PostAuthBootstrapCommandsDeferred)
        {
            await _sendAsync(command, cancellationToken).ConfigureAwait(false);
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }

        // Re-subscribe previously subscribed pairs after reconnect
        foreach (var pair in _state.SubscribedPairs)
        {
            await _sendAsync(BinollaFraming.BuildAssetChange(pair, 60), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Engine.IO MESSAGE + Socket.IO CONNECT → "40" or "40{...}" / "40/ns,{...}".
    /// Must not match 41–46 (disconnect/event/ack/error/binary).
    /// </summary>
    internal static bool IsSocketIoNamespaceConnect(string message)
    {
        if (string.IsNullOrEmpty(message) || message[0] != '4')
            return false;
        if (message.Length < 2 || message[1] != '0')
            return false;
        if (message.Length == 2)
            return true;
        var c = message[2];
        return c is '{' or '/' or ',';
    }

    private void HandleBinaryHeader(string message)
    {
        try
        {
            var jsonPart = message.Split('-', 2)[1];
            var arr = JsonConvert.DeserializeObject<List<object>>(jsonPart);
            if (arr is { Count: > 0 })
                _upcomingMessageType = arr[0]?.ToString() ?? string.Empty;
        }
        catch
        {
            _upcomingMessageType = string.Empty;
        }
    }

    private Task ProcessEventPayloadAsync(string messageType, string content)
    {
        // #region agent log
        if (messageType.Contains("history", StringComparison.OrdinalIgnoreCase) ||
            messageType.Contains("quote", StringComparison.OrdinalIgnoreCase) ||
            messageType.Contains("balance", StringComparison.OrdinalIgnoreCase) ||
            messageType.Contains("asset", StringComparison.OrdinalIgnoreCase))
        {
            LoginTrace.Write("H121", "SessionMessageRouter.ProcessEventPayloadAsync", "event", new
            {
                messageType = messageType.Length > 48 ? messageType[..48] : messageType,
                contentLen = content?.Length ?? 0,
                prefix = content is { Length: > 0 }
                    ? content[..Math.Min(24, content.Length)]
                    : ""
            });
        }
        // #endregion

        switch (messageType)
        {
            case BinollaWire.EvBalanceUpdate:
                ProcessBalanceUpdate(UnwrapPayload(content));
                break;
            case BinollaWire.EvBalancesList:
                ProcessBalanceList(UnwrapPayload(content));
                break;
            case BinollaWire.EvOrdersOpen:
                ProcessOrderOpen(UnwrapPayload(content));
                break;
            case BinollaWire.EvOrdersOpenFailed:
                ProcessOrderFailed(UnwrapPayload(content));
                break;
            case BinollaWire.EvOrdersClose:
            case BinollaWire.EvOrdersClosedList:
                ProcessOrderClose(UnwrapPayload(content));
                break;
            case BinollaWire.EvAssetsList:
                ProcessAssetsList(UnwrapPayload(content));
                break;
            case BinollaWire.EvQuotesList:
                ProcessQuotesList(UnwrapPayload(content));
                break;
            case BinollaWire.EvHistoryLast:
                ProcessHistoryLast(UnwrapPayload(content));
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Binary attachments sometimes arrive as a JSON string, or as ["eventName", payload].
    /// Must NOT unwrap raw assets/quotes matrices ([[row],[row],...]) — that destroyed
    /// s_assets/list (PM2: Session restore connected then assets count=0 forever).
    /// </summary>
    private static string UnwrapPayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var trimmed = content.Trim();
        try
        {
            if (trimmed.StartsWith('"'))
            {
                var inner = JsonConvert.DeserializeObject<string>(trimmed);
                if (!string.IsNullOrWhiteSpace(inner))
                    trimmed = inner.Trim();
            }

            if (trimmed.StartsWith('['))
            {
                var arr = JsonConvert.DeserializeObject<JArray>(trimmed);
                // Only Socket.IO-style ["event", payload] — first element must be the event name.
                if (arr is { Count: >= 2 } &&
                    arr[0]?.Type == JTokenType.String &&
                    arr[1] is not null &&
                    arr[1]!.Type is JTokenType.Object or JTokenType.Array)
                {
                    return arr[1]!.ToString(Formatting.None);
                }
            }
        }
        catch
        {
            // keep original
        }

        return trimmed;
    }

    private void ProcessBalanceUpdate(string content)
    {
        var balanceData = JsonConvert.DeserializeObject<Dictionary<string, object>>(content);
        if (balanceData is null || !balanceData.TryGetValue("balance", out var balObj))
            return;

        var balance = Convert.ToDecimal(balObj);
        bool isDemo;
        var isDemoValue = balanceData["isDemo"];
        if (isDemoValue is bool b) isDemo = b;
        else isDemo = Convert.ToInt32(isDemoValue) == 1;

        _state.UpdateSingleBalance(isDemo, balance);
    }

    private void ProcessBalanceList(string content)
    {
        try
        {
            var balanceData = JsonConvert.DeserializeObject<JObject>(content);
            if (balanceData is null)
            {
                LoginTrace.Write("H122", "SessionMessageRouter.ProcessBalanceList", "balance_null", new { len = content?.Length ?? 0 });
                return;
            }

            decimal? demo = balanceData["demoBalance"]?.Value<decimal?>()
                            ?? balanceData["demo"]?.Value<decimal?>()
                            ?? balanceData["DemoBalance"]?.Value<decimal?>();
            decimal? real = balanceData["liveBalance"]?.Value<decimal?>()
                            ?? balanceData["realBalance"]?.Value<decimal?>()
                            ?? balanceData["live"]?.Value<decimal?>()
                            ?? balanceData["real"]?.Value<decimal?>();

            // Some payloads nest under "balance".
            if (demo is null && real is null && balanceData["balance"] is JObject nested)
            {
                demo = nested["demo"]?.Value<decimal?>() ?? nested["demoBalance"]?.Value<decimal?>();
                real = nested["live"]?.Value<decimal?>() ?? nested["liveBalance"]?.Value<decimal?>();
            }

            if (demo is null && real is null)
            {
                LoginTrace.Write("H122", "SessionMessageRouter.ProcessBalanceList", "balance_keys_miss", new
                {
                    keys = string.Join(",", balanceData.Properties().Select(p => p.Name).Take(8))
                });
                return;
            }

            _state.UpdateBalance(demo, real);
            LoginTrace.Write("H122", "SessionMessageRouter.ProcessBalanceList", "balance_ok", new
            {
                hasDemo = demo is not null,
                hasReal = real is not null
            });
        }
        catch (Exception ex)
        {
            LoginTrace.Write("H122", "SessionMessageRouter.ProcessBalanceList", "balance_parse_fail", new
            {
                type = ex.GetType().Name
            });
        }
    }

    private void ProcessOrderOpen(string content)
    {
        var order = JsonConvert.DeserializeObject<OpenedOrderWire>(content);
        if (order?.Deal is null) return;

        if (_orders.TryCompleteOpenSuccess(order, _state.AccountType, out _))
            _state.Touch();
    }

    private void ProcessOrderFailed(string content)
    {
        var failed = JsonConvert.DeserializeObject<FailedOrderOpenWire>(content);
        if (failed is null) return;
        _orders.TryCompleteOpenFailure(failed);
    }

    private void ProcessOrderClose(string content)
    {
        try
        {
            if (content.TrimStart().StartsWith('['))
            {
                var deals = JsonConvert.DeserializeObject<List<DealWire>>(content) ?? new();
                foreach (var deal in deals)
                    ApplyClosedDeal(deal);
            }
            else
            {
                var closed = JsonConvert.DeserializeObject<ClosedOrderWire>(content);
                if (closed?.Deals is null) return;
                foreach (var deal in closed.Deals)
                    ApplyClosedDeal(deal);
            }
        }
        catch (JsonException)
        {
            try
            {
                var deal = JsonConvert.DeserializeObject<DealWire>(content);
                if (deal is not null)
                    ApplyClosedDeal(deal);
            }
            catch
            {
                // ignore malformed
            }
        }
    }

    private void ApplyClosedDeal(DealWire deal)
    {
        if (string.IsNullOrWhiteSpace(deal.Uuid))
            return;

        var pnl = (decimal)deal.Profit;
        _state.ClosedOrderPnL[deal.Uuid] = pnl;

        var result = pnl > 0 ? TradeResult.Win : pnl < 0 ? TradeResult.Loss : TradeResult.Tie;
        var outcome = new TradeOutcome
        {
            OrderId = deal.Uuid,
            ProfitLoss = pnl,
            ClosePrice = (decimal)deal.ClosePrice,
            ClosedAt = DateTimeOffset.UtcNow,
            Result = result
        };

        _orders.TryCompleteOutcome(deal.Uuid, pnl, (decimal)deal.ClosePrice);
        _onOrderClosed?.Invoke(outcome);
    }

    private void ProcessAssetsList(string content)
    {
        var assetsData = JsonConvert.DeserializeObject<List<List<object>>>(content);
        if (assetsData is null) return;

        var list = new List<AssetDataWire>();
        foreach (var assetArray in assetsData)
        {
            try
            {
                if (assetArray is null || assetArray.Count < 15) continue;

                var asset = new AssetDataWire
                {
                    ActiveId = Convert.ToInt32(assetArray[0]),
                    Name = assetArray[1]?.ToString() ?? string.Empty,
                    Description = assetArray.Count > 2 ? assetArray[2]?.ToString() ?? string.Empty : string.Empty,
                    Type = assetArray.Count > 3 ? assetArray[3]?.ToString() ?? string.Empty : string.Empty,
                    Precision = assetArray.Count > 4 ? Convert.ToInt32(assetArray[4]) : 0,
                    Payout = assetArray.Count > 5 ? Convert.ToInt32(assetArray[5]) : 0,
                    IsOpen = Convert.ToBoolean(assetArray[14]),
                    TradeType = assetArray.Count > 28 && assetArray[28]?.ToString() == "fixed_time"
                        ? TradeType.FixedTime
                        : TradeType.Blitz
                };
                list.Add(asset);
            }
            catch
            {
                // skip bad rows
            }
        }

        _state.ReplaceAssets(list);
        // #region agent log
        LoginTrace.Write("H124", "SessionMessageRouter.ProcessAssetsList", "assets_parsed", new
        {
            count = list.Count,
            sample = list.Take(5).Select(a => a.Name).ToArray()
        });
        // #endregion
    }

    private void ProcessQuotesList(string content)
    {
        var quotesData = JsonConvert.DeserializeObject<List<List<object>>>(content);
        if (quotesData is null) return;

        foreach (var quoteArray in quotesData)
        {
            try
            {
                if (quoteArray.Count < 3) continue;
                var pair = quoteArray[0]?.ToString();
                if (string.IsNullOrWhiteSpace(pair)) continue;
                if (!double.TryParse(quoteArray[1]?.ToString(), out var ts)) continue;
                if (!double.TryParse(quoteArray[2]?.ToString(), out var price)) continue;
                var additional = quoteArray.Count > 3 ? quoteArray[3] : null;

                _state.LatestQuotes[pair] = new QuoteData
                {
                    Pair = pair,
                    Timestamp = ts,
                    Price = price,
                    AdditionalData = additional,
                    ReceivedAt = DateTimeOffset.UtcNow
                };
            }
            catch
            {
                // skip
            }
        }
    }

    private void ProcessHistoryLast(string content)
    {
        try
        {
            var historyMessage = JsonConvert.DeserializeObject<JObject>(content);
            if (historyMessage is null)
            {
                LoginTrace.Write("H126", "SessionMessageRouter.ProcessHistoryLast", "history_null", new
                {
                    len = content?.Length ?? 0,
                    prefix = content is { Length: > 0 } ? content[..Math.Min(40, content.Length)] : ""
                });
                return;
            }

            var asset = historyMessage["asset"]?.ToString()
                        ?? historyMessage["pair"]?.ToString()
                        ?? historyMessage["symbol"]?.ToString();
            if (string.IsNullOrWhiteSpace(asset))
            {
                LoginTrace.Write("H126", "SessionMessageRouter.ProcessHistoryLast", "history_no_asset", new
                {
                    keys = string.Join(",", historyMessage.Properties().Select(p => p.Name).Take(8))
                });
                return;
            }

            var period = historyMessage["period"]?.ToObject<int>() ?? 60;
            var history = new HistoryData
            {
                Asset = asset,
                Period = period,
                ReceivedAt = DateTimeOffset.UtcNow
            };

            var historyArray = historyMessage["history"] as JArray;
            if (historyArray is not null)
            {
                foreach (var item in historyArray)
                {
                    if (item is not JArray arr || arr.Count < 2) continue;
                    history.TickHistory.Add(new TickData
                    {
                        Timestamp = arr[0]!.Value<double>(),
                        Price = arr[1]!.Value<double>(),
                        AdditionalData = arr.Count > 2 ? arr[2] : null
                    });
                }
            }

            var candlesArray = historyMessage["candles"] as JArray;
            if (candlesArray is not null)
            {
                foreach (var item in candlesArray)
                {
                    if (item is not JArray arr || arr.Count < 5) continue;
                    // Format: [timestamp, open, low, high, close, volume?, end?]  (upstream)
                    var open = arr[1]!.Value<double>();
                    var low = arr[2]!.Value<double>();
                    var high = arr[3]!.Value<double>();
                    var close = arr[4]!.Value<double>();
                    // Guard against swapped high/low from wire variants.
                    if (low > high) (low, high) = (high, low);
                    high = Math.Max(high, Math.Max(open, close));
                    low = Math.Min(low, Math.Min(open, close));
                    history.Candles.Add(new CandlestickData
                    {
                        Timestamp = arr[0]!.Value<double>(),
                        Open = open,
                        Low = low,
                        High = high,
                        Close = close,
                        Volume = arr.Count > 5 ? arr[5]?.Value<double?>() : null,
                        EndTimestamp = arr.Count > 6 ? arr[6]?.Value<double?>() : null
                    });
                }
            }

            // Some OTC pushes only tick history — synthesize OHLC so the chart can render.
            if (history.Candles.Count == 0 && history.TickHistory.Count > 0)
            {
                var bucket = Math.Max(1, period);
                long? curBucket = null;
                double open = 0, high = 0, low = 0, close = 0;
                foreach (var tick in history.TickHistory.OrderBy(t => t.Timestamp))
                {
                    var ts = (long)tick.Timestamp;
                    var b = ts - (ts % bucket);
                    if (curBucket is null || curBucket != b)
                    {
                        if (curBucket is not null)
                        {
                            history.Candles.Add(new CandlestickData
                            {
                                Timestamp = curBucket.Value,
                                Open = open,
                                High = high,
                                Low = low,
                                Close = close
                            });
                        }

                        open = high = low = close = tick.Price;
                        curBucket = b;
                    }
                    else
                    {
                        high = Math.Max(high, tick.Price);
                        low = Math.Min(low, tick.Price);
                        close = tick.Price;
                    }
                }

                if (curBucket is not null)
                {
                    history.Candles.Add(new CandlestickData
                    {
                        Timestamp = curBucket.Value,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close
                    });
                }
            }

            _state.HistoricalData[$"{asset}:{period}"] = history;
            // #region agent log
            LoginTrace.Write("H113", "SessionMessageRouter.ProcessHistoryLast", "history_stored", new
            {
                asset,
                period,
                candleCount = history.Candles.Count,
                tickCount = history.TickHistory.Count,
                synthesized = history.Candles.Count > 0 && history.TickHistory.Count > 0,
                cacheSize = _state.HistoricalData.Count
            });
            // #endregion
        }
        catch (Exception ex)
        {
            LoginTrace.Write("H126", "SessionMessageRouter.ProcessHistoryLast", "history_parse_fail", new
            {
                type = ex.GetType().Name,
                len = content?.Length ?? 0
            });
        }
    }
}
