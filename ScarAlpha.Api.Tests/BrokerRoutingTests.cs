using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Broker selection has to be safe for accounts that predate it.
///
/// <para>Every existing link was created before a broker could be chosen, so anything
/// missing or unrecognised must resolve to Binolla. Getting this wrong would point live
/// users at a venue they never signed up for.</para>
/// </summary>
public sealed class BrokerRoutingTests
{
    [Theory]
    [InlineData("binolla")]
    [InlineData("BINOLLA")]
    [InlineData("  Binolla  ")]
    public void Binolla_is_recognised_however_it_is_written(string value)
    {
        Brokers.Normalize(value).Should().Be(Brokers.Binolla);
        Brokers.IsKnown(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("quotex")]
    [InlineData("QuoteX")]
    public void Quotex_is_recognised_however_it_is_written(string value)
    {
        Brokers.Normalize(value).Should().Be(Brokers.Quotex);
        Brokers.IsKnown(value).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("iqoption")]
    public void Anything_unknown_falls_back_to_binolla(string? value)
    {
        // Links created before the broker column existed carry no value at all.
        Brokers.Normalize(value).Should().Be(Brokers.Binolla);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("iqoption")]
    public void IsKnown_stays_honest_about_unknown_values(string? value)
    {
        // Normalize is forgiving so routing always works; IsKnown is not, so a request
        // asking for a venue we do not support can still be rejected rather than silently
        // sent to Binolla.
        Brokers.IsKnown(value).Should().BeFalse();
    }

    [Fact]
    public void The_default_is_binolla_and_both_brokers_are_listed()
    {
        Brokers.Default.Should().Be(Brokers.Binolla);
        Brokers.All.Should().BeEquivalentTo(new[] { Brokers.Binolla, Brokers.Quotex });
    }

    [Fact]
    public void Credentials_default_to_the_live_balance()
    {
        // Demo is admin-granted; a session opened without saying otherwise must be live.
        new BrokerCredentials().AccountType
            .Should().Be(ScarAlpha.Binolla.Models.AccountType.Real);
    }
}
