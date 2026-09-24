namespace ScarAlpha.Application.Common;

/// <summary>Normalize bot trading-pair lists to FX currency pairs only.</summary>
public static class BotAssetList
{
    /// <summary>Hard ceiling — enough for full Binolla FX lists.</summary>
    public const int MaxAssets = 2000;

    public const string AllSymbol = "*";

    public static bool IsAll(IReadOnlyList<string>? assets) =>
        assets is not null && assets.Any(a => string.Equals(a.Trim(), AllSymbol, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Trim(), "all", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Normalize(string? asset, IEnumerable<string>? assets)
    {
        var set = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (string.Equals(trimmed, AllSymbol, StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!set.Contains(AllSymbol)) set.Insert(0, AllSymbol);
                return;
            }
            if (!FxCurrencyAssets.IsCurrencyPair(trimmed))
                return;
            if (set.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)))
                return;
            if (set.Count >= MaxAssets) return;
            set.Add(trimmed);
        }

        if (assets is not null)
        {
            foreach (var item in assets)
                Add(item);
        }

        Add(asset);
        if (set.Contains(AllSymbol))
            return new[] { AllSymbol };
        return set;
    }

    public static IReadOnlyList<string> FromStored(string? asset, IReadOnlyList<string>? assets)
    {
        var list = Normalize(asset, assets);
        return list.Count > 0 ? list : Array.Empty<string>();
    }

    public static bool Contains(IReadOnlyList<string> assets, string symbol)
    {
        if (IsAll(assets)) return true;
        if (assets.Any(a => string.Equals(a, symbol, StringComparison.OrdinalIgnoreCase)))
            return true;
        var twin = OtcTwin(symbol);
        return twin is not null &&
               assets.Any(a => string.Equals(a, twin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>EURUSD ↔ EURUSD_otc — the signal and the user's list often disagree on the suffix.</summary>
    public static string? OtcTwin(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var trimmed = symbol.Trim();
        return trimmed.EndsWith("_otc", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed + "_otc";
    }
}
