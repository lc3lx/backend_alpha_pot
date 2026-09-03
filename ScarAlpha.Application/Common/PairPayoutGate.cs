using ScarAlpha.Binolla.Abstractions;

namespace ScarAlpha.Application.Common;

/// <summary>
/// Blocks analysis and entries on Binolla pairs whose payout percentage is below the
/// configured minimum (default 75%). Pairs are re-evaluated on every snapshot because
/// Binolla pushes live payout updates on <c>s_assets/list</c>.
/// </summary>
public static class PairPayoutGate
{
    public const int DefaultMinPayoutPercent = 75;

    private static int _minPayoutPercent = DefaultMinPayoutPercent;

    /// <summary>Tunable via <c>Strategy:MinPairPayoutPercent</c>. Zero disables the rule.</summary>
    public static int MinPayoutPercent
    {
        get => _minPayoutPercent;
        set => _minPayoutPercent = value is >= 0 and <= 100
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Min payout must be 0-100.");
    }

    public static bool IsTradable(int payoutPercent) =>
        MinPayoutPercent <= 0 || payoutPercent >= MinPayoutPercent;

    /// <summary>
    /// Unknown payout (null) is allowed so a missing assets snapshot does not freeze
    /// every pair. Known values below the minimum are blocked.
    /// </summary>
    public static bool IsTradable(int? payoutPercent) =>
        payoutPercent is not int p || IsTradable(p);

    public static bool IsTradable(IReadOnlyDictionary<string, int> payoutMap, string asset)
    {
        if (MinPayoutPercent <= 0) return true;
        if (string.IsNullOrWhiteSpace(asset)) return false;
        return !payoutMap.TryGetValue(asset.Trim(), out var payout) || IsTradable(payout);
    }

    public static async Task<int?> TryGetPayoutAsync(
        IBinollaClient client,
        string asset,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asset)) return null;
        var map = await BuildPayoutMapAsync(client, ct).ConfigureAwait(false);
        return map.TryGetValue(asset.Trim(), out var payout) ? payout : null;
    }

    public static async Task<IReadOnlyDictionary<string, int>> BuildPayoutMapAsync(
        IBinollaClient client,
        CancellationToken ct = default)
    {
        var assets = await client.GetTradingAssetsAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            if (asset.PayoutPercentage <= 0 || string.IsNullOrWhiteSpace(asset.Symbol)) continue;
            map[asset.Symbol.Trim()] = asset.PayoutPercentage;
        }

        return map;
    }

    public static IReadOnlyList<string> FilterTradableSymbols(
        IReadOnlyList<string> symbols,
        IReadOnlyDictionary<string, int> payoutMap) =>
        symbols.Where(s => IsTradable(payoutMap, s)).ToList();
}
