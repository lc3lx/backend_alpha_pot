using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Infrastructure.Workers;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The promise this suite exists to hold the code to: when a setup appears and ten
/// people have the bot running, ten trades go out — not one.
///
/// <para>The old failure was never that the market disagreed. It was that each user was
/// evaluated on their own clock, so the entry aged out somewhere down the list. These
/// tests pin the two properties that rule that out: everyone selects from ONE shared
/// decision, and nothing in the per-user path is shared state that one user can consume
/// on everyone else's behalf.</para>
/// </summary>
public sealed class BotFanOutTests
{
    private static readonly DateTimeOffset BarClose = new(2026, 8, 26, 10, 7, 0, TimeSpan.Zero);

    [Fact]
    public void Ten_bots_with_the_same_pairs_all_take_the_same_entry()
    {
        var decision = Decision(("AUDCHF_otc", "Put"), ("EURUSD_otc", "Call"), ("GBPUSD_otc", "Put"));
        var pairs = new[] { "AUDCHF_otc", "EURUSD_otc", "GBPUSD_otc" };

        var picks = Enumerable.Range(0, 10)
            .Select(_ => BotSignalWorker.SelectForBot(pairs, decision))
            .ToList();

        picks.Should().OnlyContain(p => p != null);
        picks.Select(p => p!.Asset).Distinct().Should().ContainSingle()
            .Which.Should().Be("AUDCHF_otc");
        // Same signal instance, so nobody can be trading a different direction or price.
        picks.Should().OnlyContain(p => ReferenceEquals(p!.Signal, decision.Candidates[0].Signal));
    }

    [Fact]
    public void A_hundred_bots_still_agree()
    {
        var decision = Decision(("USDJPY_otc", "Call"), ("EURAUD_otc", "Put"));
        var pairs = new[] { "USDJPY_otc", "EURAUD_otc" };

        var picks = Enumerable.Range(0, 100)
            .AsParallel()
            .Select(_ => BotSignalWorker.SelectForBot(pairs, decision)!.Asset)
            .Distinct()
            .ToList();

        picks.Should().ContainSingle().Which.Should().Be("USDJPY_otc");
    }

    [Fact]
    public void Selection_ignores_the_order_the_user_listed_their_pairs_in()
    {
        var decision = Decision(("AUDCHF_otc", "Put"), ("EURUSD_otc", "Call"));

        // Rank comes from the decision, never from the user's own list order.
        BotSignalWorker.SelectForBot(new[] { "EURUSD_otc", "AUDCHF_otc" }, decision)!
            .Asset.Should().Be("AUDCHF_otc");
        BotSignalWorker.SelectForBot(new[] { "AUDCHF_otc", "EURUSD_otc" }, decision)!
            .Asset.Should().Be("AUDCHF_otc");
    }

    [Fact]
    public void Pair_casing_never_makes_a_user_miss_a_trade()
    {
        var decision = Decision(("AUDCHF_otc", "Put"));

        BotSignalWorker.SelectForBot(new[] { "audchf_OTC" }, decision)
            .Should().NotBeNull();
    }

    [Fact]
    public void A_user_who_follows_fewer_pairs_takes_their_best_available()
    {
        var decision = Decision(("AUDCHF_otc", "Put"), ("EURUSD_otc", "Call"));

        // Not following the top-ranked pair must not mean sitting the bar out.
        BotSignalWorker.SelectForBot(new[] { "EURUSD_otc" }, decision)!
            .Asset.Should().Be("EURUSD_otc");
    }

    [Fact]
    public void A_user_following_none_of_the_signalled_pairs_stands_down()
    {
        var decision = Decision(("AUDCHF_otc", "Put"));

        BotSignalWorker.SelectForBot(new[] { "USDCAD_otc" }, decision).Should().BeNull();
    }

    [Fact]
    public void An_empty_decision_produces_no_entry()
    {
        var empty = new CohortDecision(
            SignalCohort.For("rsi", 300), BarClose, Array.Empty<CohortCandidate>(), AssetsScanned: 12);

        BotSignalWorker.SelectForBot(new[] { "AUDCHF_otc" }, empty).Should().BeNull();
    }

    [Fact]
    public void Ranking_is_total_so_equal_setups_cannot_split_the_group()
    {
        // Same backtest, same RSI depth: without the symbol tiebreak the order would be
        // whatever the parallel scan happened to produce, and users could diverge.
        var a = new CohortCandidate("ZZZUSD_otc", Signal("ZZZUSD_otc", "Put", 78m, rate: 80m));
        var b = new CohortCandidate("AAAUSD_otc", Signal("AAAUSD_otc", "Put", 78m, rate: 80m));

        BotSignalWorker.Rank(new List<CohortCandidate> { a, b })[0].Asset
            .Should().Be("AAAUSD_otc");
        BotSignalWorker.Rank(new List<CohortCandidate> { b, a })[0].Asset
            .Should().Be("AAAUSD_otc");
    }

    [Fact]
    public void Ranking_prefers_the_stronger_backtest_then_the_deeper_zone()
    {
        var weak = new CohortCandidate("AAAUSD_otc", Signal("AAAUSD_otc", "Put", 90m, rate: 70m));
        var strongShallow = new CohortCandidate("BBBUSD_otc", Signal("BBBUSD_otc", "Put", 76m, rate: 85m));
        var strongDeep = new CohortCandidate("CCCUSD_otc", Signal("CCCUSD_otc", "Put", 88m, rate: 85m));

        BotSignalWorker.Rank(new List<CohortCandidate> { weak, strongShallow, strongDeep })
            .Select(c => c.Asset)
            .Should().Equal("CCCUSD_otc", "BBBUSD_otc", "AAAUSD_otc");
    }

    /// <summary>
    /// One user's open-trade hold must never gate another user. This is a process-global
    /// static, so "is it actually keyed by user" is worth pinning rather than assuming.
    /// </summary>
    [Fact]
    public void One_users_open_trade_hold_does_not_block_the_other_nine()
    {
        var users = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in users) OpenTradeGate.ReleaseUser(id);

        try
        {
            OpenTradeGate.MarkUserHeld(users[0], durationSeconds: 300);

            OpenTradeGate.IsUserHeld(users[0]).Should().BeTrue();
            users.Skip(1).Should().OnlyContain(id => !OpenTradeGate.IsUserHeld(id));
        }
        finally
        {
            foreach (var id in users) OpenTradeGate.ReleaseUser(id);
        }
    }

    [Fact]
    public void Releasing_one_user_leaves_the_others_held()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        try
        {
            OpenTradeGate.MarkUserHeld(a, 300);
            OpenTradeGate.MarkUserHeld(b, 300);
            OpenTradeGate.ReleaseUser(a);

            OpenTradeGate.IsUserHeld(a).Should().BeFalse();
            OpenTradeGate.IsUserHeld(b).Should().BeTrue();
        }
        finally
        {
            OpenTradeGate.ReleaseUser(a);
            OpenTradeGate.ReleaseUser(b);
        }
    }

    private static CohortDecision Decision(params (string Asset, string Direction)[] candidates) =>
        new(
            SignalCohort.For("rsi", 300),
            BarClose,
            candidates
                .Select(c => new CohortCandidate(
                    c.Asset,
                    Signal(c.Asset, c.Direction, c.Direction == "Put" ? 78m : 21m, rate: 80m)))
                .ToList(),
            AssetsScanned: candidates.Length);

    private static StrategySignal Signal(string asset, string direction, decimal rsi, decimal rate) =>
        new(
            StrategyId: "rsi",
            Asset: asset,
            Signal: direction,
            Rsi: rsi,
            CandleTime: BarClose,
            Timeframe: "1m",
            Backtest: new RsiBacktestStats(
                TotalSignals: 4,
                SuccessfulSignals: 3,
                FailedSignals: 1,
                SuccessRate: rate,
                LookbackCandles: 200,
                ExpiryCandles: 5,
                MinimumSuccessRate: 75m,
                Passed: true));
}
