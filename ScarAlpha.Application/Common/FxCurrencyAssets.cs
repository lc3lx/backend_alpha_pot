using System.Text.RegularExpressions;

namespace ScarAlpha.Application.Common;

/// <summary>
/// Forex / currency pairs only — excludes crypto, equities, indices, and commodities.
/// </summary>
public static partial class FxCurrencyAssets
{
    /// <summary>
    /// Currency codes recognised in a pair symbol.
    ///
    /// <para>This list was written when Binolla was the only venue, and Binolla trades
    /// majors. Quotex's book is mostly EXOTIC OTC pairs — USDBDT, USDPKR, USDEGP, USDNGN,
    /// USDARS — and every code missing here silently deleted that pair from the app. The
    /// visible symptom was a Quotex user offered five pairs out of dozens.</para>
    ///
    /// <para>The broker's own category is the better answer and is checked first; this
    /// list is the fallback for a venue that does not send one.</para>
    /// </summary>
    private static readonly HashSet<string> FxCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Majors and common crosses.
        "EUR", "GBP", "USD", "AUD", "CAD", "CHF", "JPY", "NZD",
        "TRY", "MXN", "ZAR", "SGD", "HKD", "CNH", "CNY", "SEK", "NOK",
        "PLN", "DKK", "INR", "BRL", "RUB", "CZK", "HUF", "ILS", "THB",
        // Middle East and North Africa.
        "AED", "SAR", "QAR", "OMR", "BHD", "KWD", "JOD", "LBP", "IQD", "SYP",
        "EGP", "MAD", "DZD", "TND", "LYD",
        // Africa.
        "NGN", "KES", "GHS", "UGX", "TZS", "ZMW", "XOF", "XAF", "ETB",
        // Asia-Pacific.
        "KRW", "TWD", "MYR", "IDR", "PHP", "VND", "PKR", "BDT", "LKR", "NPR", "MMK",
        // Americas.
        "ARS", "COP", "CLP", "PEN", "UYU", "BOB", "DOP", "GTQ", "CRC", "PYG",
        // Europe and the rest.
        "UAH", "RON", "BGN", "HRK", "RSD", "ISK", "KZT", "UZS", "AZN", "GEL", "AMD",
        "MDL", "BYN", "ALL", "MKD", "TMT", "KGS", "TJS", "MNT"
    };

    /// <summary>Categories a broker uses for FX. Anything here is a currency pair, whatever the symbol looks like.</summary>
    private static readonly HashSet<string> CurrencyCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "currency", "currencies", "forex", "fx", "currency_pair", "currencypair"
    };

    /// <summary>Categories that are definitely NOT FX, whatever the symbol looks like.</summary>
    private static readonly HashSet<string> NonCurrencyCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "stock", "stocks", "equity", "equities",
        "crypto", "cryptocurrency", "cryptocurrencies",
        "index", "indices", "indice",
        "commodity", "commodities", "metal", "metals", "energy"
    };

    public static bool IsCurrencyPair(string? symbol, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        if (!string.IsNullOrWhiteSpace(category))
        {
            // The broker knows what it is selling. Trusting it beats matching the symbol
            // against a hand-kept list of currency codes, which can only ever be behind.
            var c = NormalizeCategory(category);
            if (NonCurrencyCategories.Contains(c))
                return false;
            if (CurrencyCategories.Contains(c))
                return true;
        }

        return MatchesFxSymbol(symbol);
    }

    /// <summary>
    /// Strips a category down to a comparable token.
    ///
    /// <para>A broker may send `currency`, `CURRENCY`, `currency_pair`, or the name of an
    /// enum member such as `AssetType.CURRENCY`. All of those mean the same thing, and
    /// treating the last two as unknown sent those assets down the symbol-matching path
    /// for no reason.</para>
    /// </summary>
    private static string NormalizeCategory(string category)
    {
        var c = category.Trim();
        var dot = c.LastIndexOf('.');
        if (dot >= 0 && dot < c.Length - 1)
            c = c[(dot + 1)..];
        return c;
    }

    public static bool MatchesFxSymbol(string symbol)
    {
        var s = symbol.Trim().Replace("/", "", StringComparison.Ordinal);

        // OTC books are marked differently by different venues; all of these mean the
        // same pair. Missing one drops the whole OTC half of a broker's list.
        foreach (var suffix in OtcSuffixes)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^suffix.Length];
                break;
            }
        }

        s = s.Trim('-', '_', ' ');

        if (s.Length != 6 || !FxSymbolRegex().IsMatch(s))
            return false;

        return FxCodes.Contains(s[..3]) && FxCodes.Contains(s[3..]);
    }

    private static readonly string[] OtcSuffixes = { "_otc", "-otc", " otc", "otc" };

    public static IReadOnlyList<string> FilterSymbols(IEnumerable<string> symbols) =>
        symbols.Where(s => IsCurrencyPair(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [GeneratedRegex("^[A-Za-z]{6}$")]
    private static partial Regex FxSymbolRegex();
}
