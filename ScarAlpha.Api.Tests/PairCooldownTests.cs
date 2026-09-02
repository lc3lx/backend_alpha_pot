using FluentAssertions;
using ScarAlpha.Application.Common;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// After a losing entry the pair sits out for an hour, and it sits out for everyone at
/// once — a bench that applied to one account and not another would split trades that
/// are supposed to be identical.
/// </summary>
public sealed class PairCooldownTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly int _originalCooldown = PairCooldownRegistry.CooldownSeconds;

    public PairCooldownTests()
    {
        PairCooldownRegistry.Clear();
        PairCooldownRegistry.CooldownSeconds = PairCooldownRegistry.DefaultCooldownSeconds;
    }

    public void Dispose()
    {
        PairCooldownRegistry.Clear();
        PairCooldownRegistry.CooldownSeconds = _originalCooldown;
    }

    [Fact]
    public void A_loss_benches_the_pair_for_an_hour()
    {
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now);

        PairCooldownRegistry.IsBenched("rsi", "EURJPY_otc", Now.AddMinutes(59)).Should().BeTrue();
        PairCooldownRegistry.IsBenched("rsi", "EURJPY_otc", Now.AddMinutes(61)).Should().BeFalse();
    }

    [Fact]
    public void Only_the_losing_pair_is_benched()
    {
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now);

        PairCooldownRegistry.IsBenched("rsi", "GBPUSD_otc", Now.AddMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void Each_strategy_keeps_its_own_bench()
    {
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now);

        // The EMA engine reads the same pair differently; one engine's loss says nothing
        // about the other's setup.
        PairCooldownRegistry.IsBenched("ema", "EURJPY_otc", Now.AddMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void Pair_and_strategy_matching_ignores_casing()
    {
        PairCooldownRegistry.RecordLoss("RSI", "eurjpy_OTC", Now);

        PairCooldownRegistry.IsBenched("rsi", "EURJPY_otc", Now.AddMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void A_second_loss_extends_the_bench_and_never_shortens_it()
    {
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now);
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now.AddMinutes(30));

        // Freed 30 minutes after the second loss, not the first.
        PairCooldownRegistry.IsBenched("rsi", "EURJPY_otc", Now.AddMinutes(61)).Should().BeTrue();
        PairCooldownRegistry.IsBenched("rsi", "EURJPY_otc", Now.AddMinutes(91)).Should().BeFalse();
    }

    [Fact]
    public void A_stale_loss_cannot_re_bench_a_freed_pair()
    {
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now.AddMinutes(30));
        // A late settlement for an older trade must not pull the release time backwards.
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now);

        PairCooldownRegistry.IsBenched("rsi", "EURJPY_otc", Now.AddMinutes(75)).Should().BeTrue();
    }

    [Fact]
    public void Zero_seconds_disables_the_rule()
    {
        PairCooldownRegistry.CooldownSeconds = 0;
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now);

        PairCooldownRegistry.IsBenched("rsi", "EURJPY_otc", Now.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void Evict_only_drops_benches_that_have_expired()
    {
        PairCooldownRegistry.RecordLoss("rsi", "EURJPY_otc", Now);
        PairCooldownRegistry.RecordLoss("rsi", "GBPUSD_otc", Now.AddMinutes(50));

        PairCooldownRegistry.Evict(Now.AddMinutes(70));

        PairCooldownRegistry.IsBenched("rsi", "EURJPY_otc", Now.AddMinutes(70)).Should().BeFalse();
        PairCooldownRegistry.IsBenched("rsi", "GBPUSD_otc", Now.AddMinutes(70)).Should().BeTrue();
    }

    [Theory]
    [InlineData("bot:rsi:EURJPY_OTC:1756852200:Call", "rsi")]
    [InlineData("bot:ema:GBPUSD_OTC:1756852200:Put", "ema")]
    [InlineData("bot:alt5:AUDCHF_OTC:1756852200:Put", "alt5")]
    public void Strategy_is_recovered_from_the_bot_idempotency_key(string key, string expected)
    {
        // The Trade row has no strategy column; the key it was placed under is the record.
        PairCooldownRegistry.StrategyFromBotKey(key).Should().Be(expected);
    }

    [Theory]
    [InlineData("manual-trade-123")]
    [InlineData("")]
    [InlineData(null)]
    public void A_non_bot_key_yields_no_strategy(string? key)
    {
        // Trades a person placed by hand must never bench a pair for the bot.
        PairCooldownRegistry.StrategyFromBotKey(key).Should().BeNull();
    }
}
