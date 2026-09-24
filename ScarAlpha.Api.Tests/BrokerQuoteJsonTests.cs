using FluentAssertions;
using ScarAlpha.Infrastructure.BrokerGateway;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class BrokerQuoteJsonTests
{
    [Fact]
    public void Parses_gateway_tick_dump()
    {
        const string json =
            """[{"asset":"EURUSD","timestamp":1790002320.0,"price":1.1444},{"asset":"USDMXN_otc","timestamp":1790002328.5,"price":18.251}]""";

        var rows = BrokerQuoteJson.Parse(json, new DateTimeOffset(2026, 9, 21, 14, 52, 0, TimeSpan.Zero));

        rows.Should().HaveCount(2);
        rows[0].Pair.Should().Be("EURUSD");
        rows[0].Price.Should().Be(1.1444);
        rows[1].Pair.Should().Be("USDMXN_otc");
        rows[1].Price.Should().Be(18.251);
    }
}
