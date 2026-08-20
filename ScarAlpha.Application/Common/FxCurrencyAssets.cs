using System.Text.RegularExpressions;

namespace ScarAlpha.Application.Common;

/// <summary>
/// Forex / currency pairs only — excludes crypto, equities, indices, and commodities.
/// </summary>
public static partial class FxCurrencyAssets
{
    private static readonly HashSet<string> FxCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EUR", "GBP", "USD", "AUD", "CAD", "CHF", "JPY", "NZD",
        "TRY", "MXN", "ZAR", "SGD", "HKD", "CNH", "CNY", "SEK", "NOK",
        "PLN", "DKK", "INR", "BRL", "RUB", "CZK", "HUF", "ILS", "THB"
    };

    public static bool IsCurrencyPair(string? symbol, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        if (!string.IsNullOrWhiteSpace(category))
        {
            var c = category.Trim();
            if (c.Equals("stock", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("crypto", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("cryptocurrency", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("index", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("commodity", StringComparison.OrdinalIgnoreCase))
                return false;

            if (c.Equals("currency", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return MatchesFxSymbol(symbol);
    }

    public static bool MatchesFxSymbol(string symbol)
    {
        var s = symbol.Trim().Replace("/", "", StringComparison.Ordinal);
        if (s.EndsWith("_otc", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];

        if (s.Length != 6 || !FxSymbolRegex().IsMatch(s))
            return false;

        return FxCodes.Contains(s[..3]) && FxCodes.Contains(s[3..]);
    }

    public static IReadOnlyList<string> FilterSymbols(IEnumerable<string> symbols) =>
        symbols.Where(s => IsCurrencyPair(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [GeneratedRegex("^[A-Za-z]{6}$")]
    private static partial Regex FxSymbolRegex();
}
