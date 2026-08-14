using System.Text.RegularExpressions;
using Newtonsoft.Json;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Protocol;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Binolla.Transport;

namespace ScarAlpha.Binolla.Tests.Support;

/// <summary>
/// Minimal Binolla Socket.IO simulator for deterministic multi-user tests.
/// </summary>
internal sealed class SimulatedBinollaEndpoint
{
    private readonly FakeWebSocketTransport _transport;
    private readonly object _emitGate = new();
    private int _orderSeq;

    public SimulatedBinollaEndpoint(
        FakeWebSocketTransport transport,
        decimal demoBalance = 1000m,
        decimal realBalance = 50m,
        string? forceFailSsidMarker = "INVALID")
    {
        _transport = transport;
        DemoBalance = demoBalance;
        RealBalance = realBalance;
        FailSsidMarker = forceFailSsidMarker;
        _transport.OnClientMessage += OnClientMessageAsync;
    }

    public decimal DemoBalance { get; }
    public decimal RealBalance { get; }
    public string? FailSsidMarker { get; }
    public bool AutoCloseOrders { get; set; }
    public int AutoCloseDelayMs { get; set; } = 20;

    private async Task OnClientMessageAsync(string message)
    {
        if (message == "40")
        {
            _transport.InjectServerMessage("""40{"sid":"sim-ns"}""");
            return;
        }

        if (message.Contains("authorization", StringComparison.OrdinalIgnoreCase))
        {
            if (FailSsidMarker is not null &&
                message.Contains(FailSsidMarker, StringComparison.OrdinalIgnoreCase))
            {
                _transport.InjectServerMessage("""42["NotAuthorized"]""");
                return;
            }

            _transport.InjectServerMessage("""42["s_authorization",{"ok":true}]""");
            return;
        }

        if (message.Contains("assets/list", StringComparison.Ordinal))
        {
            EmitBinary(BinollaWire.EvAssetsList,
                """[[1,"EURUSD","EUR/USD","currency",5,80,null,null,null,0,null,null,null,0,true,null,0,0,80,null,0,0,0,0,0,0,0,0,"fixed_time"],[2,"GBPUSD_otc","GBP/USD OTC","currency",5,85,null,null,null,0,null,null,null,0,true,null,0,0,85,null,0,0,0,0,0,0,0,0,"fixed_time"]]""");
            return;
        }

        if (message.Contains("asset/change", StringComparison.Ordinal))
        {
            var assetMatch = Regex.Match(message, "\"asset\":\"([^\"]+)\"");
            var periodMatch = Regex.Match(message, "\"period\":(\\d+)");
            var asset = assetMatch.Success ? assetMatch.Groups[1].Value : "EURUSD";
            var period = periodMatch.Success ? periodMatch.Groups[1].Value : "60";
            var price = asset.Contains("GBP", StringComparison.OrdinalIgnoreCase) ? "1.34567" : "1.23456";

            EmitBinary(BinollaWire.EvQuotesList,
                "[[\"" + asset + "\",1710000000.5," + price + "]]");
            EmitBinary(BinollaWire.EvHistoryLast,
                "{\"asset\":\"" + asset + "\",\"period\":" + period +
                ",\"history\":[[1710000000,1.23],[1710000060,1.24]],\"candles\":[[1710000000,1.23,1.22,1.24,1.235],[1710000060,1.235,1.23,1.25,1.24]]}");
            return;
        }

        if (message.Contains("orders/opened/list", StringComparison.Ordinal)
            || message.Contains("orders/closed/list", StringComparison.Ordinal)
            || message.Contains("alert/", StringComparison.Ordinal)
            || message.Contains("indicator/list", StringComparison.Ordinal)
            || message.Contains("drawing/load", StringComparison.Ordinal))
        {
            return;
        }

        if (message.Contains("account/change", StringComparison.Ordinal))
        {
            EmitBinary(
                BinollaWire.EvBalancesList,
                "{\"demoBalance\":" + DemoBalance.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"liveBalance\":" + RealBalance.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            return;
        }

        if (message.Contains("orders/open", StringComparison.Ordinal))
        {
            await HandleOrderOpenAsync(message).ConfigureAwait(false);
            return;
        }

        if (message == "2")
            _transport.InjectServerMessage("3");
    }

    private async Task HandleOrderOpenAsync(string message)
    {
        var match = Regex.Match(message, """42\["orders/open",(\{.*\})\]""");
        if (!match.Success)
            return;

        var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(match.Groups[1].Value)
                      ?? new();

        var asset = payload.TryGetValue("asset", out var a) ? a?.ToString() ?? "EURUSD" : "EURUSD";
        var amount = payload.TryGetValue("amount", out var am) ? Convert.ToDecimal(am) : 1m;
        var cmd = payload.TryGetValue("cmd", out var c) ? Convert.ToInt32(c) : 0;
        var requestId = payload.TryGetValue("requestId", out var rid) ? Convert.ToInt32(rid) : 0;

        if (asset.Equals("FAIL_ASSET", StringComparison.OrdinalIgnoreCase))
        {
            var fail = new
            {
                error = "Market closed",
                isDemo = true,
                requestId,
                amount,
                asset,
                time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            EmitBinary(BinollaWire.EvOrdersOpenFailed, JsonConvert.SerializeObject(fail));
            return;
        }

        // Globally unique so two independent session simulators never collide.
        var uuid = $"order-{Guid.NewGuid():N}-{Interlocked.Increment(ref _orderSeq)}-r{requestId}";
        var openJson =
            "{\"deal\":{" +
            "\"uuid\":\"" + uuid + "\"," +
            "\"uid\":1," +
            "\"openPrice\":1.1," +
            "\"command\":" + cmd + "," +
            "\"amount\":" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"percentProfit\":80," +
            "\"asset\":\"" + asset + "\"," +
            "\"isDemo\":true," +
            "\"requestId\":" + requestId + "," +
            "\"profit\":" + (amount * 0.8m).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"openTimestamp\":1," +
            "\"closeTimestamp\":2," +
            "\"closePrice\":0," +
            "\"balance\":" + DemoBalance.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "}}";

        EmitBinary(BinollaWire.EvOrdersOpen, openJson);

        if (AutoCloseOrders)
        {
            // Fire-and-forget close so PlaceOrder SendAsync is not held during delay,
            // and concurrent opens do not stall behind each other's auto-close.
            _ = Task.Run(async () =>
            {
                await Task.Delay(AutoCloseDelayMs).ConfigureAwait(false);
                var profit = amount * 0.8m;
                var closeJson =
                    "{\"profit\":" + profit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"deals\":[{\"uuid\":\"" + uuid + "\",\"profit\":" +
                    profit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"closePrice\":1.101,\"asset\":\"" + asset + "\",\"amount\":" +
                    amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}]}";
                EmitBinary(BinollaWire.EvOrdersClose, closeJson);
            });
        }
    }

    private void EmitBinary(string eventType, string jsonPayload)
    {
        // Keep header+payload atomic under concurrent PlaceOrder simulations.
        lock (_emitGate)
        {
            _transport.InjectServerMessage("451-[\"" + eventType + "\"]");
            _transport.InjectServerMessage(jsonPayload, asBinary: true);
        }
    }
}

internal static class SessionTestFactory
{
    public static async Task<BinollaSession> ConnectSimulatedAsync(
        string userId,
        string ssid,
        decimal demo = 1000m,
        decimal real = 50m,
        bool autoCloseOrders = false,
        bool enableReconnect = false,
        CancellationToken ct = default)
    {
        var transportReady = new TaskCompletionSource<FakeWebSocketTransport>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new BinollaSessionManagerOptions
        {
            EnableAutoReconnect = enableReconnect,
            DefaultOperationTimeout = TimeSpan.FromSeconds(8),
            PlaceOrderTimeout = TimeSpan.FromSeconds(5),
            OutcomeTimeout = TimeSpan.FromSeconds(15),
            EnableChartConnection = false,
            MaxReconnectAttempts = 2
        };

        var session = new BinollaSession(userId, options, () =>
        {
            var transport = new FakeWebSocketTransport();
            _ = new SimulatedBinollaEndpoint(transport, demo, real)
            {
                AutoCloseOrders = autoCloseOrders,
                AutoCloseDelayMs = 30
            };
            transportReady.TrySetResult(transport);
            return transport;
        });

        var connectTask = session.ConnectAsync(ssid, ct);
        var transport = await transportReady.Task.WaitAsync(ct).ConfigureAwait(false);

        // Engine.IO open packet — kicks Socket.IO handshake
        transport.InjectServerMessage("""0{"sid":"sim","upgrades":[]}""");

        await connectTask.ConfigureAwait(false);

        // Allow bootstrap balance/assets events to settle
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (session.State.BalanceUpdatedAt is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20, ct).ConfigureAwait(false);

        return session;
    }
}
