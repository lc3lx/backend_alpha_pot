namespace ScarAlpha.Binolla.Models;

public sealed class BalanceInfo
{
    public decimal RealBalance { get; init; }
    public decimal DemoBalance { get; init; }
    public AccountType CurrentType { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public string Currency { get; init; } = "USD";

    public decimal CurrentBalance =>
        CurrentType == AccountType.Demo ? DemoBalance : RealBalance;
}

public sealed class OrderResponse
{
    public required string OrderId { get; init; }
    public required string Asset { get; init; }
    public TradeDirection Direction { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset ExpiryTime { get; init; }
    public DateTimeOffset PlacedAt { get; init; }
    public decimal? OpenPrice { get; init; }
    public decimal ExpectedPayout { get; init; }
    public OrderStatus Status { get; init; }
    public AccountType BalanceType { get; init; }
    public int RequestId { get; init; }
}

public sealed class TradeOutcome
{
    public required string OrderId { get; init; }
    public decimal ProfitLoss { get; init; }
    public decimal? ClosePrice { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public TradeResult Result { get; init; }

    public bool IsWin => Result == TradeResult.Win;
    public bool IsLoss => Result == TradeResult.Loss;
}

public sealed class TradingAsset
{
    public required string Symbol { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool IsOpen { get; init; }
    public int PayoutPercentage { get; init; }
    public string Category { get; init; } = "currency";
}

public sealed class QuoteData
{
    public string Pair { get; init; } = string.Empty;
    public double Timestamp { get; init; }
    public double Price { get; init; }
    public object? AdditionalData { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset QuoteTime =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(Timestamp * 1000));
}

public sealed class TickData
{
    public double Timestamp { get; init; }
    public double Price { get; init; }
    public object? AdditionalData { get; init; }
}

public sealed class CandlestickData
{
    public double Timestamp { get; init; }
    public double Open { get; init; }
    public double Low { get; init; }
    public double High { get; init; }
    public double Close { get; init; }
    public double? Volume { get; init; }
    public double? EndTimestamp { get; init; }
}

public sealed class HistoryData
{
    public string Asset { get; init; } = string.Empty;
    public int Period { get; init; }
    public List<TickData> TickHistory { get; init; } = new();
    public List<CandlestickData> Candles { get; init; } = new();
    public CandlestickData? LatestCandle => Candles.Count > 0 ? Candles[^1] : null;
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AccessedAt { get; set; } = DateTimeOffset.UtcNow;
}
