using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The multi-user guarantee: everyone running the same strategy for the same duration
/// acts on ONE decision, taken once per bar — not on whatever the market looked like
/// when their own turn in a loop came round.
/// </summary>
public sealed class CohortSignalCacheTests
{
    private static readonly DateTimeOffset BarClose = new(2026, 8, 26, 10, 7, 0, TimeSpan.Zero);
    private static readonly SignalCohort Rsi5m = SignalCohort.For("rsi", 300);

    [Fact]
    public async Task Hundred_users_in_one_cohort_share_a_single_scan()
    {
        var cache = new CohortSignalCache();
        var scans = 0;
        var now = BarClose.AddSeconds(2);

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            cache.GetOrAddAsync(Rsi5m, 60, now, (bar, _) =>
            {
                Interlocked.Increment(ref scans);
                return Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put")));
            })));

        scans.Should().Be(1);
        results.Should().OnlyContain(d => ReferenceEquals(d, results[0]));
        results[0]!.Candidates[0].Asset.Should().Be("EURUSD_otc");
    }

    [Fact]
    public async Task Every_user_sees_the_same_ranked_list()
    {
        var cache = new CohortSignalCache();
        var now = BarClose.AddSeconds(3);

        var first = await cache.GetOrAddAsync(Rsi5m, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("GBPUSD_otc", "Call"), ("EURUSD_otc", "Put"))));

        // A user arriving 20 seconds later, still inside the same bar, must not re-scan
        // into a different answer.
        var late = await cache.GetOrAddAsync(Rsi5m, 60, now.AddSeconds(20), (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("AUDCHF_otc", "Put"))));

        late.Should().BeSameAs(first);
        late!.Candidates.Select(c => c.Asset)
            .Should().Equal("GBPUSD_otc", "EURUSD_otc");
    }

    [Fact]
    public async Task Different_strategies_decide_independently()
    {
        var cache = new CohortSignalCache();
        var now = BarClose.AddSeconds(2);
        var ema = SignalCohort.For("ema", 300);

        var rsiDecision = await cache.GetOrAddAsync(Rsi5m, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put"))));
        var emaDecision = await cache.GetOrAddAsync(ema, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("USDJPY_otc", "Call"), ema)));

        emaDecision.Should().NotBeSameAs(rsiDecision);
        emaDecision!.Candidates[0].Asset.Should().Be("USDJPY_otc");
    }

    [Fact]
    public async Task Same_strategy_with_different_durations_are_separate_cohorts()
    {
        var cache = new CohortSignalCache();
        var now = BarClose.AddSeconds(2);
        var scans = 0;

        await cache.GetOrAddAsync(SignalCohort.For("rsi", 300), 60, now, (bar, _) =>
        {
            Interlocked.Increment(ref scans);
            return Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put"), SignalCohort.For("rsi", 300)));
        });
        await cache.GetOrAddAsync(SignalCohort.For("rsi", 60), 60, now, (bar, _) =>
        {
            Interlocked.Increment(ref scans);
            return Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put"), SignalCohort.For("rsi", 60)));
        });

        // Options are derived from the trade duration, so these genuinely are different
        // decisions and must not share a cache slot.
        scans.Should().Be(2);
    }

    [Fact]
    public async Task Next_bar_forces_a_fresh_decision()
    {
        var cache = new CohortSignalCache();

        var first = await cache.GetOrAddAsync(Rsi5m, 60, BarClose.AddSeconds(2), (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put"))));
        var next = await cache.GetOrAddAsync(Rsi5m, 60, BarClose.AddSeconds(63), (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("GBPUSD_otc", "Call"))));

        next.Should().NotBeSameAs(first);
        next!.ClosedBarTime.Should().Be(BarClose.AddMinutes(1));
    }

    [Fact]
    public async Task An_empty_market_is_cached_but_a_failed_scan_is_not()
    {
        var cache = new CohortSignalCache();
        var now = BarClose.AddSeconds(2);

        // Scanned pairs but found nothing tradable — a real answer, worth keeping.
        await cache.GetOrAddAsync(Rsi5m, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(
                new CohortDecision(Rsi5m, bar, Array.Empty<CohortCandidate>(), AssetsScanned: 18)));
        cache.TryGet(Rsi5m, 60, now).Should().NotBeNull();

        // Reached no pair at all (session down) — must not pin "no trades" for the bar.
        var other = SignalCohort.For("ema", 300);
        await cache.GetOrAddAsync(other, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(
                new CohortDecision(other, bar, Array.Empty<CohortCandidate>(), AssetsScanned: 0)));
        cache.TryGet(other, 60, now).Should().BeNull();
    }

    [Fact]
    public async Task A_null_scan_is_retried_by_the_next_user()
    {
        var cache = new CohortSignalCache();
        var now = BarClose.AddSeconds(2);

        var failed = await cache.GetOrAddAsync(Rsi5m, 60, now, (_, _) =>
            Task.FromResult<CohortDecision?>(null));
        failed.Should().BeNull();

        var retried = await cache.GetOrAddAsync(Rsi5m, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put"))));
        retried!.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Evict_drops_stale_decisions()
    {
        var cache = new CohortSignalCache();
        var now = BarClose.AddSeconds(2);

        await cache.GetOrAddAsync(Rsi5m, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put"))));

        cache.Evict(BarClose.AddMinutes(10), 60);
        cache.TryGet(Rsi5m, 60, now).Should().BeNull();
    }

    [Fact]
    public void Cohort_key_is_case_insensitive_on_strategy()
    {
        SignalCohort.For("RSI", 300).Should().Be(SignalCohort.For("rsi", 300));
        SignalCohort.For(null, 300).Should().Be(SignalCohort.For("rsi", 300));
    }

    private static CohortDecision Decision(
        DateTimeOffset bar,
        params (string Asset, string Direction)[] candidates) =>
        Decision(bar, candidates, Rsi5m);

    private static CohortDecision Decision(
        DateTimeOffset bar,
        (string Asset, string Direction) candidate,
        SignalCohort cohort) =>
        Decision(bar, new[] { candidate }, cohort);

    private static CohortDecision Decision(
        DateTimeOffset bar,
        (string Asset, string Direction)[] candidates,
        SignalCohort cohort) =>
        new(
            cohort,
            bar,
            candidates
                .Select(c => new CohortCandidate(c.Asset, Signal(c.Asset, c.Direction, bar)))
                .ToList(),
            AssetsScanned: Math.Max(candidates.Length, 1));

    private static StrategySignal Signal(string asset, string direction, DateTimeOffset bar) =>
        new(
            StrategyId: "rsi",
            Asset: asset,
            Signal: direction,
            Rsi: direction == "Put" ? 78m : 21m,
            CandleTime: bar,
            Timeframe: "1m");
}
