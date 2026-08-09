using Newtonsoft.Json;

namespace ScarAlpha.Binolla.Models;

/// <summary>
/// Wire models preserved from BinollaApiDotNetPro DTOs (JSON shapes).
/// </summary>
public sealed class OpenOrderDealWire
{
    [JsonProperty("uuid")]
    public string Uuid { get; set; } = string.Empty;

    [JsonProperty("uid")]
    public int Uid { get; set; }

    [JsonProperty("openTime")]
    public string? OpenTime { get; set; }

    [JsonProperty("closeTime")]
    public string? CloseTime { get; set; }

    [JsonProperty("openPrice")]
    public double OpenPrice { get; set; }

    [JsonProperty("command")]
    public int Command { get; set; }

    [JsonProperty("amount")]
    public double Amount { get; set; }

    [JsonProperty("percentProfit")]
    public int PercentProfit { get; set; }

    [JsonProperty("asset")]
    public string Asset { get; set; } = string.Empty;

    [JsonProperty("isDemo")]
    public bool IsDemo { get; set; }

    [JsonProperty("requestId")]
    public int RequestId { get; set; }

    [JsonProperty("profit")]
    public double Profit { get; set; }

    [JsonProperty("openTimestamp")]
    public long OpenTimestamp { get; set; }

    [JsonProperty("closeTimestamp")]
    public long CloseTimestamp { get; set; }

    [JsonProperty("closePrice")]
    public double ClosePrice { get; set; }

    [JsonProperty("balance")]
    public double Balance { get; set; }
}

public sealed class OpenedOrderWire
{
    [JsonProperty("deal")]
    public OpenOrderDealWire? Deal { get; set; }
}

public sealed class FailedOrderOpenWire
{
    [JsonProperty("error")]
    public string Error { get; set; } = string.Empty;

    [JsonProperty("isDemo")]
    public bool IsDemo { get; set; }

    [JsonProperty("requestId")]
    public int RequestId { get; set; }

    [JsonProperty("amount")]
    public double Amount { get; set; }

    [JsonProperty("asset")]
    public string Asset { get; set; } = string.Empty;

    [JsonProperty("time")]
    public long Time { get; set; }
}

public sealed class DealWire
{
    [JsonProperty("uuid")]
    public string Uuid { get; set; } = string.Empty;

    [JsonProperty("profit")]
    public double Profit { get; set; }

    [JsonProperty("closePrice")]
    public double ClosePrice { get; set; }

    [JsonProperty("asset")]
    public string? Asset { get; set; }

    [JsonProperty("amount")]
    public double Amount { get; set; }
}

public sealed class ClosedOrderWire
{
    [JsonProperty("profit")]
    public double Profit { get; set; }

    [JsonProperty("deals")]
    public List<DealWire> Deals { get; set; } = new();
}

public sealed class AssetDataWire
{
    public int ActiveId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Precision { get; set; }
    public int Payout { get; set; }
    public bool IsOpen { get; set; }
    public TradeType? TradeType { get; set; }
}
