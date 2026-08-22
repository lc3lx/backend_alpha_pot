using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The multi-account guarantee: every user analysing the same pair in the same minute
/// gets one identical result, computed once.
/// </summary>
public sealed class MarketAnalysisCacheTests
{
    private const string Asset = "EURUSD_otc";
    private static readonly DateTimeOffset BarClose = new(2026, 8, 5, 10, 7, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_accounts_in_the_same_minute_share_one_computation()
    {
        var cache = new MarketAnalysisCache();
        var computed = 0;
        var now = BarClose.AddSeconds(2);

        var accountA = await cache.GetOrAddAsync(Asset, 60, now, _ =>
        {
            Interlocked.Increment(ref computed);
            return Task.FromResult<MarketAnalysis?>(Analysis(BarClose, rsi: 71.42m));
        });

        var accountB = await cache.GetOrAddAsync(Asset, 60, now, _ =>
        {
            Interlocked.Increment(ref computed);
            return Task.FromResult<MarketAnalysis?>(Analysis(BarClose, rsi: 99m));
        });

        computed.Should().Be(1);
        accountB.Should().BeSameAs(accountA);
        accountB!.Rsi.Should().Be(71.42m);
    }

    [Fact]
    public async Task A_hundred_concurrent_accounts_still_fetch_once()
    {
        var cache = new MarketAnalysisCache();
        var computed = 0;
        var now = BarClose.AddSeconds(1);

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            cache.GetOrAddAsync(Asset, 60, now, async _ =>
            {
                Interlocked.Increment(ref computed);
                await Task.Delay(15);
                return (MarketAnalysis?)Analysis(BarClose, rsi: 26m);
            })));

        computed.Should().Be(1);
        results.Should().OnlyContain(r => r != null && r.Rsi == 26m);
        results.Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_new_bar_invalidates_the_previous_analysis()
    {
        var cache = new MarketAnalysisCache();
        var computed = 0;

        Task<MarketAnalysis?> Produce(DateTimeOffset bar) =>
            cache.GetOrAddAsync(Asset, 60, bar.AddSeconds(3), _ =>
            {
                Interlocked.Increment(ref computed);
                return Task.FromResult<MarketAnalysis?>(Analysis(bar, rsi: 50m));
            });

        await Produce(BarClose);
        await Produce(BarClose);
        await Produce(BarClose.AddMinutes(1));

        computed.Should().Be(2);
    }

    [Fact]
    public async Task A_stale_analysis_is_returned_but_never_pinned_for_other_accounts()
    {
        var cache = new MarketAnalysisCache();
        var now = BarClose.AddSeconds(2);

        // Producer hands back an analysis for an older bar (lagging history feed).
        var stale = await cache.GetOrAddAsync(Asset, 60, now, _ =>
            Task.FromResult<MarketAnalysis?>(Analysis(BarClose.AddMinutes(-2), rsi: 12m)));

        stale.Should().NotBeNull();
        cache.TryGet(Asset, 60, now).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_analysis_is_not_cached_so_the_next_account_retries()
    {
        var cache = new MarketAnalysisCache();
        var attempts = 0;
        var now = BarClose.AddSeconds(2);

        Task<MarketAnalysis?> Attempt(bool succeed) =>
            cache.GetOrAddAsync(Asset, 60, now, _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(succeed ? Analysis(BarClose, rsi: 33m) : null);
            });

        (await Attempt(succeed: false)).Should().BeNull();
        (await Attempt(succeed: true))!.Rsi.Should().Be(33m);

        attempts.Should().Be(2);
    }

    [Fact]
    public void Different_pairs_do_not_share_an_entry()
    {
        MarketAnalysisCache.CurrentClosedBarTime(BarClose.AddSeconds(59), 60).Should().Be(BarClose);
        MarketAnalysisCache.CurrentClosedBarTime(BarClose.AddSeconds(60), 60).Should().Be(BarClose.AddMinutes(1));
    }

    private static MarketAnalysis Analysis(DateTimeOffset barClose, decimal rsi)
    {
        var stats = new RsiBacktestStats(1, 1, 0, 100m, 200, 5, 75m, true);
        var candle = new RsiCandle(barClose.AddMinutes(-1), 100m, barClose);
        return new MarketAnalysis(
            Asset: Asset,
            TimeframeSeconds: 60,
            ClosedBarTime: barClose,
            ClosedCandles: new[] { candle },
            Closes: new[] { 100m },
            Rsi: rsi,
            CallBacktest: stats,
            PutBacktest: stats);
    }
}
