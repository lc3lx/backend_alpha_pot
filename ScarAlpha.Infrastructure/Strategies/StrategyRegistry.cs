using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.Strategies;

public sealed class StrategyRegistry : IStrategyRegistry
{
    private static readonly IReadOnlyList<StrategyInfo> Catalog =
    [
        new("smart", "Smart (auto by market regime)", StrategyCatalogStatus.Active, Enabled: true),
        new("rsi", "RSI Smart Backtest", StrategyCatalogStatus.Active, Enabled: true),
        new("ema", "EMA 9/21 + RSI Scalping", StrategyCatalogStatus.Active, Enabled: true),
        new("alt5", "Alternating Candles (5m)", StrategyCatalogStatus.Active, Enabled: true),
        new("macd", "MACD", StrategyCatalogStatus.ComingSoon, Enabled: false),
        new("ai", "AI", StrategyCatalogStatus.ComingSoon, Enabled: false)
    ];

    public IReadOnlyList<StrategyInfo> GetStrategies() => Catalog;

    public StrategyInfo? Get(string strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) return null;
        return Catalog.FirstOrDefault(s =>
            string.Equals(s.Id, strategyId.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
