using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.Strategies;

public sealed class StrategyRegistry : IStrategyRegistry
{
    private static readonly IReadOnlyList<StrategyInfo> Catalog =
    [
        new("rsi", "RSI", StrategyCatalogStatus.Active, Enabled: true),
        new("ema", "EMA", StrategyCatalogStatus.ComingSoon, Enabled: false),
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
