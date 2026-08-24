using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Application.Services;
using ScarAlpha.Infrastructure.Access;
using ScarAlpha.Infrastructure.Strategies;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// End-to-end selection: asking the bot to run "ema" must actually leave the bot
/// running EMA — including after a restart.
/// </summary>
public sealed class StrategySelectionTests
{
    private static readonly Guid UserId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public void Ema_is_a_runnable_strategy_in_the_catalog()
    {
        var registry = new StrategyRegistry();

        var ema = registry.Get("ema");
        ema.Should().NotBeNull();
        ema!.Status.Should().Be(StrategyCatalogStatus.Active);
        ema.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Starting_the_bot_with_ema_keeps_the_bot_on_ema()
    {
        var (control, runtime) = Build();

        var dto = await control.StartAsync(
            new BotStartRequest(Asset: "EURUSD_otc", StrategyId: "ema"), CancellationToken.None);

        dto.StrategyId.Should().Be("ema");
        runtime.Get(UserId).StrategyId.Should().Be("ema");
        runtime.Get(UserId).State.Should().Be(BotRunState.Running);
    }

    [Fact]
    public async Task Default_start_stays_on_rsi()
    {
        var (control, _) = Build();

        var dto = await control.StartAsync(
            new BotStartRequest(Asset: "EURUSD_otc"), CancellationToken.None);

        dto.StrategyId.Should().Be("rsi");
    }

    [Fact]
    public async Task A_strategy_that_is_not_released_is_rejected_not_silently_swapped()
    {
        var (control, _) = Build();

        // MACD is still ComingSoon — running RSI instead without saying so would be worse.
        var comingSoon = () => control.StartAsync(
            new BotStartRequest(Asset: "EURUSD_otc", StrategyId: "macd"), CancellationToken.None);
        (await comingSoon.Should().ThrowAsync<ApiException>())
            .Which.Code.Should().Be(ApiErrorCodes.ValidationError);

        var unknown = () => control.StartAsync(
            new BotStartRequest(Asset: "EURUSD_otc", StrategyId: "does-not-exist"), CancellationToken.None);
        (await unknown.Should().ThrowAsync<ApiException>())
            .Which.Code.Should().Be(ApiErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Switching_strategy_while_configured_is_applied()
    {
        var (control, runtime) = Build();
        await control.StartAsync(new BotStartRequest(Asset: "EURUSD_otc"), CancellationToken.None);

        control.Apply(new BotApplyRequest(StrategyId: "ema"));

        runtime.Get(UserId).StrategyId.Should().Be("ema");

        // Apply without a strategy must not reset the one already chosen.
        control.Apply(new BotApplyRequest(Amount: 30m));
        runtime.Get(UserId).StrategyId.Should().Be("ema");
    }

    [Fact]
    public void Strategy_survives_a_restart_and_old_saves_default_to_rsi()
    {
        var config = new BotRuntimeConfig(
            UserId, BotRunState.Running, "EURUSD_otc", 25m, 300, 50m, 30m,
            DateTimeOffset.UtcNow, Assets: new[] { "EURUSD_otc" }, StrategyId: "ema");

        var restored = BotRuntimeService.StoredBotRuntime.From(config).ToConfig(UserId);
        restored.StrategyId.Should().Be("ema");

        // A runtime saved before strategy selection existed has no field at all.
        var legacyJson =
            "{\"state\":\"Running\",\"assets\":[\"EURUSD_otc\"],\"amount\":25," +
            "\"durationSeconds\":300,\"dailyProfitTarget\":50,\"dailyLossLimit\":30," +
            "\"autoStopAtProfit\":true,\"autoStopAtLoss\":true," +
            "\"signalConfirmationEnabled\":true,\"riskLevel\":\"risk-medium\"," +
            "\"notificationsEnabled\":true}";
        var legacy = BotRuntimeService.StoredBotRuntime.TryParse(UserId, legacyJson);
        legacy.Should().NotBeNull();
        legacy!.StrategyId.Should().Be("rsi");
    }

    [Fact]
    public void Every_runnable_strategy_has_a_timeframe_the_broker_supports()
    {
        // Regression: the signal endpoint used to pass its legacy `period=60` query
        // default straight through, which threw for any strategy that is not 1-minute
        // and turned every poll into a 500. Callers must take the timeframe from the
        // strategy, so every strategy has to declare one the wire accepts.
        var supported = new[] { 60, 300, 900, 3600 };

        foreach (var strategy in new StrategyRegistry().GetStrategies().Where(s => s.Enabled))
        {
            var timeframe = StrategyTimeframes.For(strategy.Id);
            supported.Should().Contain(timeframe, $"strategy '{strategy.Id}' must run on a supported period");
        }
    }

    [Fact]
    public void The_alternating_strategy_is_the_five_minute_one()
    {
        StrategyTimeframes.For("alt5").Should().Be(300);
        StrategyTimeframes.For("rsi").Should().Be(60);
        StrategyTimeframes.For("ema").Should().Be(60);
        StrategyTimeframes.For("smart").Should().Be(60);
    }

    private static (BotControlAppService Control, IBotRuntimeService Runtime) Build()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new BotRuntimeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BotRuntimeService>.Instance);

        var control = new BotControlAppService(
            new FakeCurrentUser(),
            runtime,
            new AlwaysAllowedAccess(),
            new StrategyRegistry());

        return (control, runtime);
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid UserId => StrategySelectionTests.UserId;
        public long? TelegramUserId => 1;
        public bool IsAdmin => false;
    }

    private sealed class AlwaysAllowedAccess : IBotAccessService
    {
        public Task<BotAccessResult> CheckAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(new BotAccessResult(
                BotAccessState.Allowed, true, true, "Demo", "Approved"));
    }
}
