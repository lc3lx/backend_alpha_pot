using FluentAssertions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Which symbols count as tradable currency pairs.
///
/// <para>This filter decides what a user is offered. It was written against Binolla, which
/// trades majors, and silently deleted most of Quotex's book — a Quotex account was shown
/// five pairs out of dozens, with nothing anywhere saying why.</para>
/// </summary>
public sealed class FxCurrencyAssetsTests
{
    [Theory]
    [InlineData("EURUSD")]
    [InlineData("EURUSD_otc")]
    [InlineData("GBPJPY")]
    [InlineData("EUR/USD")]
    public void Majors_are_currency_pairs(string symbol) =>
        FxCurrencyAssets.IsCurrencyPair(symbol).Should().BeTrue();

    /// <summary>
    /// Quotex's book is mostly exotics. Every code missing from the list removed that
    /// pair from the app entirely.
    /// </summary>
    [Theory]
    [InlineData("USDBDT_otc")]
    [InlineData("USDPKR_otc")]
    [InlineData("USDEGP_otc")]
    [InlineData("USDNGN_otc")]
    [InlineData("USDARS_otc")]
    [InlineData("USDCOP_otc")]
    [InlineData("USDIDR_otc")]
    [InlineData("USDPHP_otc")]
    [InlineData("USDVND_otc")]
    [InlineData("USDDZD_otc")]
    [InlineData("AEDCNY_otc")]
    [InlineData("USDKZT")]
    public void Quotex_exotics_are_currency_pairs(string symbol) =>
        FxCurrencyAssets.IsCurrencyPair(symbol).Should().BeTrue();

    /// <summary>Venues mark their OTC books differently; all of them mean the same pair.</summary>
    [Theory]
    [InlineData("EURUSD_otc")]
    [InlineData("EURUSD-otc")]
    [InlineData("EURUSD_OTC")]
    [InlineData("EURUSDotc")]
    public void Every_otc_spelling_is_accepted(string symbol) =>
        FxCurrencyAssets.IsCurrencyPair(symbol).Should().BeTrue();

    [Theory]
    [InlineData("BTCUSD")]
    [InlineData("AAPL")]
    [InlineData("SP500")]
    [InlineData("XAUUSD")]
    public void Non_currency_symbols_are_rejected(string symbol) =>
        FxCurrencyAssets.IsCurrencyPair(symbol).Should().BeFalse();

    /// <summary>
    /// The broker's own category outranks the symbol list: it knows what it is selling,
    /// and a hand-kept list of currency codes can only ever be behind.
    /// </summary>
    [Theory]
    [InlineData("currency")]
    [InlineData("CURRENCY")]
    [InlineData("forex")]
    [InlineData("currency_pair")]
    [InlineData("AssetType.CURRENCY")]
    public void A_currency_category_wins_over_an_unknown_symbol(string category) =>
        FxCurrencyAssets.IsCurrencyPair("ZZZQQQ", category).Should().BeTrue();

    [Theory]
    [InlineData("crypto")]
    [InlineData("cryptocurrency")]
    [InlineData("stock")]
    [InlineData("indices")]
    [InlineData("commodity")]
    [InlineData("AssetType.CRYPTO")]
    public void A_non_currency_category_wins_over_a_currency_looking_symbol(string category) =>
        FxCurrencyAssets.IsCurrencyPair("EURUSD", category).Should().BeFalse();
}
