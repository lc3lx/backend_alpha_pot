namespace ScarAlpha.Application.Common;

/// <summary>Normalize bot trading-pair lists (all available market pairs; hard ceiling for safety).</summary>
public static class BotAssetList
{
    /// <summary>Hard ceiling only — high enough to cover full Binolla asset lists.</summary>
    public const int MaxAssets = 2000;

    public static IReadOnlyList<string> Normalize(string? asset, IEnumerable<string>? assets)
    {
        var set = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
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
        return set;
    }

    public static IReadOnlyList<string> FromStored(string? asset, IReadOnlyList<string>? assets)
    {
        var list = Normalize(asset, assets);
        return list.Count > 0 ? list : Array.Empty<string>();
    }

    public static bool Contains(IReadOnlyList<string> assets, string symbol) =>
        assets.Any(a => string.Equals(a, symbol, StringComparison.OrdinalIgnoreCase));
}
