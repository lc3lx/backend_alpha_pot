using FluentAssertions;
using ScarAlpha.Application.Services;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class TimeSignalParserTests
{
    private const string SampleAdminSignals = """
        USD/IDR-OTC - 22:34 - CALL 
        USD/EGP-OTC - 22:42 - PUT 
        USD/NGN-OTC - 22:52 - CALL 
        USD/INR-OTC - 23:06 - PUT 
        NZD/JPY-OTC - 23:21 - PUT 
        USD/COP-OTC - 23:28 - CALL 
        NZD/CHF-OTC - 23:43 - CALL 
        USD/DZD-OTC - 23:57 - PUT 
        USD/MXN-OTC - 00:00 - PUT 
        USD/BDT-OTC - 00:05 - PUT 
        NZD/CAD-OTC - 00:10 - PUT 
        USD/ARS-OTC - 00:15 - CAL
        """;

    [Fact]
    public void Parses_all_12_admin_signals_accurately()
    {
        var parsed = TimeSignalAppService.ParseRawSignals(
            SampleAdminSignals,
            timeZoneOffsetMinutes: 180, // UTC+3
            baseDateStr: "2026-09-16",
            durationSeconds: 60);

        parsed.Should().HaveCount(12);
        parsed.Should().OnlyContain(p => p.IsValid);

        // Signal 1: USD/IDR-OTC - 22:34 - CALL
        parsed[0].Asset.Should().Be("USDIDR_otc");
        parsed[0].Direction.Should().Be("CALL");
        parsed[0].ScheduledTimeLocalFormatted.Should().Be("2026-09-16 22:34");
        // 22:34 at UTC+3 is 19:34 UTC
        parsed[0].ScheduledTimeUtc.Hour.Should().Be(19);
        parsed[0].ScheduledTimeUtc.Minute.Should().Be(34);

        // Signal 2: USD/EGP-OTC - 22:42 - PUT
        parsed[1].Asset.Should().Be("USDEGP_otc");
        parsed[1].Direction.Should().Be("PUT");

        // Signal 8: USD/DZD-OTC - 23:57 - PUT
        parsed[7].Asset.Should().Be("USDDZD_otc");
        parsed[7].Direction.Should().Be("PUT");
        parsed[7].ScheduledTimeLocalFormatted.Should().Be("2026-09-16 23:57");

        // Signal 9: USD/MXN-OTC - 00:00 - PUT (Midnight rollover!)
        parsed[8].Asset.Should().Be("USDMXN_otc");
        parsed[8].Direction.Should().Be("PUT");
        parsed[8].ScheduledTimeLocalFormatted.Should().Be("2026-09-17 00:00");
        // 00:00 next day at UTC+3 is 21:00 UTC previous day
        parsed[8].ScheduledTimeUtc.Day.Should().Be(16);
        parsed[8].ScheduledTimeUtc.Hour.Should().Be(21);
        parsed[8].ScheduledTimeUtc.Minute.Should().Be(0);

        // Signal 12: USD/ARS-OTC - 00:15 - CAL (Typo CAL corrected to CALL)
        parsed[11].Asset.Should().Be("USDARS_otc");
        parsed[11].Direction.Should().Be("CALL");
        parsed[11].ScheduledTimeLocalFormatted.Should().Be("2026-09-17 00:15");
    }

    [Theory]
    [InlineData("EUR/USD", "EURUSD")]
    [InlineData("EUR/USD-OTC", "EURUSD_otc")]
    [InlineData("usd/idr-otc", "USDIDR_otc")]
    [InlineData("NZDJPY_otc", "NZDJPY_otc")]
    [InlineData("USD/ARS_OTC", "USDARS_otc")]
    public void Normalizes_assets_correctly(string raw, string expected)
    {
        TimeSignalAppService.NormalizeAsset(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("CALL", "CALL")]
    [InlineData("CAL", "CALL")]
    [InlineData("UP", "CALL")]
    [InlineData("BUY", "CALL")]
    [InlineData("C", "CALL")]
    [InlineData("PUT", "PUT")]
    [InlineData("DOWN", "PUT")]
    [InlineData("SELL", "PUT")]
    [InlineData("P", "PUT")]
    public void Normalizes_direction_correctly(string raw, string expected)
    {
        TimeSignalAppService.NormalizeDirection(raw).Should().Be(expected);
    }
}
