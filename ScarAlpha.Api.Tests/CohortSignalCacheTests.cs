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
    private static readonly SignalCohort Rsi5m = SignalCohort.For(Brokers.Binolla, "rsi", 300);

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
        var ema = SignalCohort.For(Brokers.Binolla, "ema", 300);

        var rsiDecision = await cache.GetOrAddAsync(Rsi5m, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put"))));
        var emaDecision = await cache.GetOrAddAsync(ema, 60, now, (bar, _) =>
            Task.FromResult<CohortDecision?>(Decision(bar, ("USDJPY_otc", "Call"), ema)));

        emaDecision.Should().NotBeSameAs(rsiDecision);
        emaDecision!.Candidates[0].Asset.Should().Be("USDJPY_otc");
    }

    /// <summary>
    /// 60s and 300s both derive 5 expiry candles, so they are the SAME analysis and must
    /// share one scan. They used to be separate cohorts purely because the web app and the
    /// admin panel send different raw durations — which split the fleet in two and had the
    /// market scanned twice for an identical decision.
    /// </summary>
    [Fact]
    public async Task Durations_that_derive_the_same_options_share_one_scan()
    {
        var cache = new CohortSignalCache();
        var now = BarClose.AddSeconds(2);
        var scans = 0;

        Task<CohortDecision?> Produce(DateTimeOffset bar, CancellationToken _)
        {
            Interlocked.Increment(ref scans);
            return Task.FromResult<CohortDecision?>(
                Decision(bar, ("EURUSD_otc", "Put"), SignalCohort.For(Brokers.Binolla, "rsi", 300)));
        }

        await cache.GetOrAddAsync(SignalCohort.For(Brokers.Binolla, "rsi", 300), 60, now, Produce);
        await cache.GetOrAddAsync(SignalCohort.For(Brokers.Binolla, "rsi", 60), 60, now, Produce);

        scans.Should().Be(1);
        SignalCohort.For(Brokers.Binolla, "rsi", 60).Should().Be(SignalCohort.For(Brokers.Binolla, "rsi", 300));
    }

    /// <summary>Durations that really do differ still decide independently.</summary>
    [Fact]
    public void Durations_that_derive_different_options_stay_separate()
    {
        // 180s -> 3 expiry candles, 300s -> 5. Different backtest, different decision.
        SignalCohort.For(Brokers.Binolla, "rsi", 180).Should().NotBe(SignalCohort.For(Brokers.Binolla, "rsi", 300));
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
        var other = SignalCohort.For(Brokers.Binolla, "ema", 300);
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

    /// <summary>
    /// Two venues never share a decision.
    ///
    /// <para>EURUSD on Binolla and EURUSD on Quotex are different price series from
    /// different books. Pooling them would hand a Quotex user an entry priced on Binolla's
    /// feed — a trade taken at a price that never existed on their own account.</para>
    /// </summary>
    [Fact]
    public async Task Brokers_never_share_a_decision()
    {
        var cache = new CohortSignalCache();
        var now = BarClose.AddSeconds(2);
        var scans = 0;

        var binolla = SignalCohort.For(Brokers.Binolla, "rsi", 300);
        var quotex = SignalCohort.For(Brokers.Quotex, "rsi", 300);

        binolla.Should().NotBe(quotex);

        await cache.GetOrAddAsync(binolla, 60, now, (bar, _) =>
        {
            Interlocked.Increment(ref scans);
            return Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Put"), binolla));
        });
        await cache.GetOrAddAsync(quotex, 60, now, (bar, _) =>
        {
            Interlocked.Increment(ref scans);
            return Task.FromResult<CohortDecision?>(Decision(bar, ("EURUSD_otc", "Call"), quotex));
        });

        scans.Should().Be(2, "each venue must be scanned on its own book");
        cache.TryGet(binolla, 60, now)!.Candidates[0].Signal.Signal.Should().Be("Put");
        cache.TryGet(quotex, 60, now)!.Candidates[0].Signal.Signal.Should().Be("Call");
    }

    [Fact]
    public void Cohort_key_is_case_insensitive_on_strategy()
    {
        SignalCohort.For(Brokers.Binolla, "RSI", 300).Should().Be(SignalCohort.For(Brokers.Binolla, "rsi", 300));
        SignalCohort.For(Brokers.Binolla, null, 300).Should().Be(SignalCohort.For(Brokers.Binolla, "rsi", 300));
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
