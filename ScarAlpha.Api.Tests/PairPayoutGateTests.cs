using FluentAssertions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class PairPayoutGateTests : IDisposable
{
    private readonly int _originalMin = PairPayoutGate.MinPayoutPercent;

    public PairPayoutGateTests() => PairPayoutGate.MinPayoutPercent = PairPayoutGate.DefaultMinPayoutPercent;

    public void Dispose() => PairPayoutGate.MinPayoutPercent = _originalMin;

    [Theory]
    [InlineData(74, false)]
    [InlineData(75, true)]
    [InlineData(92, true)]
    public void Known_payout_is_blocked_below_minimum(int payout, bool tradable)
    {
        PairPayoutGate.IsTradable(payout).Should().Be(tradable);
    }

    [Fact]
    public void Unknown_payout_is_allowed()
    {
        PairPayoutGate.IsTradable((int?)null).Should().BeTrue();
    }

    [Fact]
    public void Zero_minimum_disables_the_rule()
    {
        PairPayoutGate.MinPayoutPercent = 0;
        PairPayoutGate.IsTradable(50).Should().BeTrue();
    }

    [Fact]
    public void Filter_drops_symbols_with_low_payout()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["EURUSD_otc"] = 85,
            ["GBPUSD_otc"] = 70,
        };

        var filtered = PairPayoutGate.FilterTradableSymbols(
            new[] { "EURUSD_otc", "GBPUSD_otc", "AUDCHF_otc" },
            map);

        filtered.Should().BeEquivalentTo(new[] { "EURUSD_otc", "AUDCHF_otc" });
    }
}
