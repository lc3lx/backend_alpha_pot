using System.Text.Json;
using System.Text.Json.Serialization;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Entities;

namespace ScarAlpha.Application.Services;

/// <summary>
/// Serialize / normalize admin marketing-demo display config stored on <see cref="User.MarketingDemoConfigJson"/>.
/// </summary>
public static class MarketingDemoConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static MarketingDemoConfigDto Default { get; } = new();

    public static MarketingDemoConfigDto Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Default;

        try
        {
            var parsed = JsonSerializer.Deserialize<MarketingDemoConfigDto>(json, JsonOptions);
            return Normalize(parsed ?? Default);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static MarketingDemoConfigDto Normalize(MarketingDemoConfigDto? input)
    {
        var c = input ?? Default;
        var balance = Clamp(c.Balance, 0m, 10_000_000m, 12_450m);
        var wobble = Clamp(c.BalanceWobble, 0m, 5_000m, 28m);
        var profit = Clamp(c.TotalProfit, 0m, 10_000_000m, 3_200m);
        var loss = Clamp(c.TotalLoss, 0m, 10_000_000m, 1_100m);
        var winRate = Clamp(c.WinRatePercent, 0m, 100m, 62m);
        var count = Math.Clamp(c.HistoryTradeCount, 1, 200);
        var amount = Clamp(c.DefaultTradeAmount, 1m, 100_000m, 25m);
        var plan = string.IsNullOrWhiteSpace(c.PlanName) ? null : c.PlanName.Trim();

        IReadOnlyList<MarketingDemoTradeSeedDto>? seeds = null;
        if (c.SampleTrades is { Count: > 0 })
        {
            seeds = c.SampleTrades
                .Take(100)
                .Select(NormalizeSeed)
                .ToList();
        }

        return new MarketingDemoConfigDto(
            Balance: Math.Round(balance, 2),
            BalanceWobble: Math.Round(wobble, 2),
            TotalProfit: Math.Round(profit, 2),
            TotalLoss: Math.Round(loss, 2),
            WinRatePercent: Math.Round(winRate, 2),
            HistoryTradeCount: count,
            DefaultTradeAmount: Math.Round(amount, 2),
            IncludeRunningTrade: c.IncludeRunningTrade,
            PlanName: plan,
            SampleTrades: seeds);
    }

    public static string Serialize(MarketingDemoConfigDto config) =>
        JsonSerializer.Serialize(Normalize(config), JsonOptions);

    public static void ApplyToUser(User user, MarketingDemoConfigDto? config)
    {
        user.MarketingDemoConfigJson = Serialize(config ?? Default);
    }

    public static MarketingDemoConfigDto FromUser(User user) => Parse(user.MarketingDemoConfigJson);

    private static MarketingDemoTradeSeedDto NormalizeSeed(MarketingDemoTradeSeedDto seed)
    {
        var direction = (seed.Direction ?? "CALL").Trim().ToUpperInvariant();
        direction = direction is "PUT" or "DOWN" ? "PUT" : "CALL";
        var status = (seed.Status ?? "Profit").Trim();
        if (status is not ("Profit" or "Loss" or "Tie" or "Running" or "Pending"))
            status = "Profit";

        var amount = Clamp(seed.Amount, 1m, 100_000m, 25m);
        decimal? pnl = seed.Pnl;
        if (pnl is null && status is "Profit" or "Loss" or "Tie")
        {
            pnl = status switch
            {
                "Profit" => Math.Round(amount * 0.87m, 2),
                "Loss" => -amount,
                _ => 0m
            };
        }

        return new MarketingDemoTradeSeedDto(
            Asset: string.IsNullOrWhiteSpace(seed.Asset) ? "EURUSD_otc" : seed.Asset.Trim(),
            Direction: direction,
            Amount: Math.Round(amount, 2),
            Status: status,
            Pnl: pnl is null ? null : Math.Round(pnl.Value, 2),
            DurationSeconds: Math.Clamp(seed.DurationSeconds, 5, 3600),
            MinutesAgo: Math.Clamp(seed.MinutesAgo, 0, 60 * 24 * 90));
    }

    private static decimal Clamp(decimal value, decimal min, decimal max, decimal fallback)
    {
        if (value < min || value > max)
            return fallback;
        return value;
    }
}
