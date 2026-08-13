using System.Globalization;

namespace ScarAlpha.Binolla.Protocol;

/// <summary>
/// Wire constants preserved from BinollaApiDotNetPro (WebSocketClientBinolla / ControlMessageProcessor / MessageProcessor).
/// </summary>
public static class BinollaWire
{
    public const string TradingSocketUri =
        "wss://ws3.binolla.com/socket.io/?EIO=4&transport=websocket";

    public const string ChartSocketUri = "wss://ws2.binolla.com/ws";

    public const string Origin = "https://binolla.com";
    public const string HostTrading = "ws3.binolla.com";
    public const string HostChart = "ws2.binolla.com";

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";

    // Event type names from MessageProcessor switch
    public const string EvBalanceUpdate = "s_balance/update";
    public const string EvBalancesList = "s_balances/list";
    public const string EvOrdersOpen = "s_orders/open";
    public const string EvOrdersOpenFailed = "f_orders/open";
    public const string EvOrdersClose = "s_orders/close";
    public const string EvOrdersClosedList = "s_orders/closed/list";
    public const string EvAssetsList = "s_assets/list";
    public const string EvQuotesList = "s_quotes/list";
    public const string EvHistoryLast = "s_history/last";
    public const string EvAuthorization = "s_authorization";

    public static readonly string[] PostAuthBootstrapCommands =
    {
        "42[\"account/change\",{\"demo\":1}]",
        "42[\"balances/list\"]",
        "42[\"orders/opened/list\"]",
        "42[\"orders/closed/list\"]",
        "42[\"assets/list\"]",
        "42[\"alert/list\"]",
        "42[\"alert/closed/list\"]",
        "42[\"indicator/list\"]",
        "42[\"drawing/load\"]"
    };

    /// <summary>Commands required for Demo market browse right after SSID auth.</summary>
    public static readonly string[] PostAuthBootstrapCommandsEssential =
    {
        "42[\"account/change\",{\"demo\":1}]",
        "42[\"balances/list\"]",
        "42[\"assets/list\"]",
    };

    /// <summary>Secondary lists — deferred so they do not race the first unauthorized window.</summary>
    public static readonly string[] PostAuthBootstrapCommandsDeferred =
    {
        "42[\"orders/opened/list\"]",
        "42[\"orders/closed/list\"]",
        "42[\"alert/list\"]",
        "42[\"alert/closed/list\"]",
        "42[\"indicator/list\"]",
        "42[\"drawing/load\"]"
    };
}

public static class BinollaFraming
{
    /// <summary>
    /// Original PlaceOrder format from BinollaApiClient (+ optional requestId for correlation).
    /// </summary>
    public static string BuildOrderOpen(string asset, int expiryUnix, decimal amount, int cmd, int requestId) =>
        "42[\"orders/open\",{\"asset\":\"" + asset + "\",\"time\":" + expiryUnix
        + ",\"amount\":" + amount.ToString(CultureInfo.InvariantCulture)
        + ",\"cmd\":" + cmd + ",\"requestId\":" + requestId + "}]";

    public static string BuildAccountChange(bool isDemo) =>
        isDemo
            ? "42[\"account/change\",{\"demo\":1}]"
            : "42[\"account/change\",{\"demo\":0}]";

    public static string BuildAssetChange(string pair, int period) =>
        "42[\"asset/change\",{\"asset\":\"" + pair + "\",\"period\":" + period + "}]";

    public static string BuildAlertList() => "42[\"alert/list\"]";
    public static string BuildAlertClosedList() => "42[\"alert/closed/list\"]";
}

/// <summary>
/// Redact tokens/SSIDs for logs. Never log full auth material.
/// </summary>
public static class SsidSafety
{
    public static string Redact(string? ssid)
    {
        if (string.IsNullOrEmpty(ssid))
            return "<empty>";

        if (ssid.Length <= 12)
            return "***";

        return $"{ssid[..4]}…{ssid[^4..]} (len={ssid.Length})";
    }

    public static bool LooksLikeAuthFrame(string message) =>
        message.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || message.Contains("\"token\"", StringComparison.OrdinalIgnoreCase);
}
