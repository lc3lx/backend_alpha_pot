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
        ProtocolTrace.Write("H12", "SessionMessageRouter.HandleRawAsync", "inbound", new
        {
            kind = ProtocolTrace.Classify(message),
            detail = ProtocolTrace.SafePrefix(message),
            lifecycle = _state.Lifecycle.ToString()
        });

        // Engine.IO OPEN — do not require "sid" substring (packet may vary).
        if (message.StartsWith('0'))
        {
            ProtocolTrace.Write("H12", "SessionMessageRouter", "sending_ns_connect_40");
            await _sendAsync("40", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Namespace connected — send auth SSID.
        // Some servers send "40" only; others send 40{"sid":"..."}.
        if (IsSocketIoNamespaceConnect(message))
        {
            var ssid = _state.Ssid
                       ?? throw new BinollaAuthenticationException("SSID is missing.");
            ProtocolTrace.Write("H12", "SessionMessageRouter", "sending_auth_frame", new
            {
                ssidLen = ssid.Length,
                hasTokenField = ssid.Contains("\"token\"", StringComparison.Ordinal)
            });
            await _sendAsync(ssid, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.StartsWith("42") && message.Contains(BinollaWire.EvAuthorization, StringComparison.Ordinal))
        {
            ProtocolTrace.Write("H15", "SessionMessageRouter", "s_authorization_text");
            await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Live evidence: Binolla may omit text s_authorization and still push post-auth events.
        if (message.StartsWith("42") && IsPostAuthSignal(message))
        {
            ProtocolTrace.Write("H15", "SessionMessageRouter", "post_auth_text_signal", new
            {
                kind = ProtocolTrace.Classify(message)
            });
            await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
            // continue — may still be a normal event with no payload
        }

        if (message == "2")
        {
            await _sendAsync("3", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.StartsWith("451-[", StringComparison.Ordinal))
        {
            HandleBinaryHeader(message);
            ProtocolTrace.Write("H12", "SessionMessageRouter", "binary_header", new
            {
                upcoming = _upcomingMessageType
            });

            // Auth success often arrives as binary event header before payload.
            if (IsPostAuthSignal(_upcomingMessageType) ||
                string.Equals(_upcomingMessageType, BinollaWire.EvAuthorization, StringComparison.Ordinal))
            {
                ProtocolTrace.Write("H15", "SessionMessageRouter", "post_auth_binary_header", new
                {
                    upcoming = _upcomingMessageType
                });
                await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (message.StartsWith("42") && message.Contains("NotAuthorized", StringComparison.Ordinal))
        {
            ProtocolTrace.Write("H13", "SessionMessageRouter", "not_authorized");
            _state.SetLifecycle(SessionLifecycleState.AuthenticationFailed, "SSID not authorized.");
            return;
        }

        // Binary payload following 451-[type] (may arrive as WS binary decoded to UTF-8 text)
        if (!string.IsNullOrEmpty(_upcomingMessageType))
        {
            var type = _upcomingMessageType;
            _upcomingMessageType = string.Empty;
            if (string.Equals(type, BinollaWire.EvAuthorization, StringComparison.Ordinal) ||
                IsPostAuthSignal(type))
            {
                ProtocolTrace.Write("H15", "SessionMessageRouter", "post_auth_payload", new { type });
                await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
            }

            await ProcessEventPayloadAsync(type, message).ConfigureAwait(false);
        }
    }

    private async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _authorized, 1) == 1)
            return;

        ProtocolTrace.Write("H15", "SessionMessageRouter", "ensure_authorized_enter", new
        {
            priorLifecycle = _state.Lifecycle.ToString()
        });

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
        return value.Contains("s_balances/", StringComparison.Ordinal)
               || value.Contains("s_assets/", StringComparison.Ordinal)
               || value.Contains("s_account/", StringComparison.Ordinal)
               || value.Contains("s_orders/", StringComparison.Ordinal)
               || value.Contains(BinollaWire.EvAuthorization, StringComparison.Ordinal);
    }

    private async Task SendPostAuthBootstrapAsync(CancellationToken cancellationToken)
    {
        foreach (var command in BinollaWire.PostAuthBootstrapCommands)
        {
            await _sendAsync(command, cancellationToken).ConfigureAwait(false);
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
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
        switch (messageType)
        {
            case BinollaWire.EvBalanceUpdate:
                ProcessBalanceUpdate(content);
                break;
            case BinollaWire.EvBalancesList:
                ProcessBalanceList(content);
                break;
            case BinollaWire.EvOrdersOpen:
                ProcessOrderOpen(content);
                break;
            case BinollaWire.EvOrdersOpenFailed:
                ProcessOrderFailed(content);
                break;
            case BinollaWire.EvOrdersClose:
            case BinollaWire.EvOrdersClosedList:
                ProcessOrderClose(content);
                break;
            case BinollaWire.EvAssetsList:
                ProcessAssetsList(content);
                break;
            case BinollaWire.EvQuotesList:
                ProcessQuotesList(content);
                break;
            case BinollaWire.EvHistoryLast:
                ProcessHistoryLast(content);
                break;
        }

        return Task.CompletedTask;
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
        var balanceData = JsonConvert.DeserializeObject<Dictionary<string, object>>(content);
        if (balanceData is null) return;

        decimal? demo = balanceData.TryGetValue("demoBalance", out var d) ? Convert.ToDecimal(d) : null;
        decimal? real = balanceData.TryGetValue("liveBalance", out var r) ? Convert.ToDecimal(r) : null;
        _state.UpdateBalance(demo, real);
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
    }

    private void ProcessQuotesList(string content)
    {
        var quotesData = JsonConvert.DeserializeObject<List<List<object>>>(content);
        if (quotesData is null) return;

        var received = new List<string>(4);
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
                if (received.Count < 4) received.Add(pair);
            }
            catch
            {
                // skip
            }
        }

        // #region agent log
        if (received.Count > 0)
        {
            ProtocolTrace.Write("H21", "SessionMessageRouter.ProcessQuotesList", "quotes_received", new
            {
                sample = received.ToArray(),
                batch = quotesData.Count,
                totalCached = _state.LatestQuotes.Count
            });
        }
        // #endregion
    }

    private void ProcessHistoryLast(string content)
    {
        var historyMessage = JsonConvert.DeserializeObject<JObject>(content);
        if (historyMessage is null) return;

        var asset = historyMessage["asset"]?.ToString();
        if (string.IsNullOrWhiteSpace(asset)) return;

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
                history.Candles.Add(new CandlestickData
                {
                    Timestamp = arr[0]!.Value<double>(),
                    Open = arr[1]!.Value<double>(),
                    Low = arr[2]!.Value<double>(),
                    High = arr[3]!.Value<double>(),
                    Close = arr[4]!.Value<double>(),
                    Volume = arr.Count > 5 ? arr[5]?.Value<double?>() : null,
                    EndTimestamp = arr.Count > 6 ? arr[6]?.Value<double?>() : null
                });
            }
        }

        _state.HistoricalData[$"{asset}:{period}"] = history;

        // #region agent log
        ProtocolTrace.Write("H21", "SessionMessageRouter.ProcessHistoryLast", "history_received", new
        {
            asset,
            period,
            candles = history.Candles.Count,
            ticks = history.TickHistory.Count
        });
        // #endregion
    }
}
