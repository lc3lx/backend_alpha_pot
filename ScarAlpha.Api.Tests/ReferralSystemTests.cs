using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Application.Services;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Notifications;
using ScarAlpha.Infrastructure.Persistence;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Referral program tests exercise the services directly against an EF InMemory
/// <see cref="AppDbContext"/> — no WebApplicationFactory/host involved, so these are
/// unaffected by the pre-existing host-bootstrap issue in <c>ApiIntegrationTests.cs</c>.
/// </summary>
public sealed class ReferralTiersTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(19, 2)]
    [InlineData(20, 3)]
    [InlineData(49, 3)]
    [InlineData(50, 4)]
    [InlineData(99, 4)]
    [InlineData(100, 5)]
    [InlineData(250, 5)]
    public void Resolves_the_highest_tier_reached(int qualifiedCount, int expectedLevel)
    {
        var tier = ReferralTiers.ResolveTier(qualifiedCount);
        (tier?.Level ?? 0).Should().Be(expectedLevel);
    }

    [Fact]
    public void Tier_rates_and_gifts_match_the_published_program()
    {
        ReferralTiers.ByLevel(1).Should().Be(new ReferralTier(1, 5, 0.15m, 25m));
        ReferralTiers.ByLevel(2).Should().Be(new ReferralTier(2, 10, 0.25m, 75m));
        ReferralTiers.ByLevel(3).Should().Be(new ReferralTier(3, 20, 0.40m, 200m));
        ReferralTiers.ByLevel(4).Should().Be(new ReferralTier(4, 50, 0.75m, 500m));
        ReferralTiers.ByLevel(5).Should().Be(new ReferralTier(5, 100, 1.00m, 1000m));
        ReferralTiers.IPhoneQualifiedReferrals.Should().Be(10);
    }
}

public sealed class ReferralSystemFixture : IDisposable
{
    public AppDbContext Db { get; }
    public IReferralRepository Referrals { get; }
    public IUserRepository Users { get; }
    public IReferralQualificationService Qualification { get; }
    public IReferralAccrualService Accrual { get; }
    public ReferralAppService App { get; }

    public ReferralSystemFixture()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        Db = new AppDbContext(options);

        Referrals = new ReferralRepository(Db);
        Users = new UserRepository(Db);
        var notifications = new NotificationWriter(new NotificationRepository(Db));
        var rewards = new ReferralRewardService(Referrals, notifications, NullLogger<ReferralRewardService>.Instance);
        Qualification = new ReferralQualificationService(Referrals, rewards, NullLogger<ReferralQualificationService>.Instance);
        Accrual = new ReferralAccrualService(Referrals, Users, Qualification, NullLogger<ReferralAccrualService>.Instance);
        App = new ReferralAppService(new FakeCurrentUser(), Users, Referrals, NullLogger<ReferralAppService>.Instance);
    }

    public async Task<User> CreateUserAsync(bool isMarketingDemo = false)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@test.local",
            IsMarketingDemo = isMarketingDemo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return await Users.AddAsync(user);
    }

    public async Task<ReferralQualification> AttachAsync(Guid referrerId, Guid referredId, DateTimeOffset windowStartedAt)
    {
        var q = new ReferralQualification
        {
            Id = Guid.NewGuid(),
            ReferrerUserId = referrerId,
            ReferredUserId = referredId,
            WindowStartedAt = windowStartedAt,
            CreatedAt = windowStartedAt,
            UpdatedAt = windowStartedAt
        };
        await Referrals.AddQualificationAsync(q);
        return q;
    }

    /// <summary>Fabricates N already-qualified referred users under one referrer, for tier/commission tests.</summary>
    public async Task GiveQualifiedReferralsAsync(Guid referrerId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var referred = await CreateUserAsync();
            var q = await AttachAsync(referrerId, referred.Id, DateTimeOffset.UtcNow.AddDays(-30));
            q.DepositMet = true;
            q.ActiveDaysCount = ReferralConfig.MinActiveDaysInWindow;
            q.Qualified = true;
            q.QualifiedAt = DateTimeOffset.UtcNow;
            await Referrals.UpdateQualificationAsync(q);
        }
    }

    public Trade BuildRealTrade(Guid userId, decimal amount, TradeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Asset = "EURUSD",
        Amount = amount,
        DurationSeconds = 300,
        AccountType = BinollaAccountType.Real,
        Status = status,
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => Db.Dispose();

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid UserId { get; set; } = Guid.NewGuid();
        public long? TelegramUserId => null;
        public bool IsAdmin => false;
    }
}

public sealed class ReferralQualificationServiceTests : IClassFixture<ReferralSystemFixture>
{
    private readonly ReferralSystemFixture _f;
    public ReferralQualificationServiceTests(ReferralSystemFixture f) => _f = f;

    [Fact]
    public async Task Deposit_alone_does_not_qualify()
    {
        var referrer = await _f.CreateUserAsync();
        var referred = await _f.CreateUserAsync();
        await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow); // window just started

        await _f.Qualification.ObserveRealBalanceAsync(referred.Id, ReferralConfig.MinDepositUsd, default);

        var q = await _f.Qualification.GetAsync(referred.Id, default);
        q!.DepositMet.Should().BeTrue();
        q.Qualified.Should().BeFalse("the 15-day window has not elapsed and there is no trading activity yet");
    }

    [Fact]
    public async Task Fifteen_days_with_no_activity_does_not_qualify()
    {
        var referrer = await _f.CreateUserAsync();
        var referred = await _f.CreateUserAsync();
        await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow.AddDays(-20));

        await _f.Qualification.ObserveRealBalanceAsync(referred.Id, ReferralConfig.MinDepositUsd, default);

        var q = await _f.Qualification.GetAsync(referred.Id, default);
        q!.Qualified.Should().BeFalse("no bot trading activity was ever recorded");
    }

    [Fact]
    public async Task Deposit_plus_window_plus_activity_qualifies()
    {
        var referrer = await _f.CreateUserAsync();
        var referred = await _f.CreateUserAsync();
        await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow.AddDays(-20));

        await _f.Qualification.ObserveRealBalanceAsync(referred.Id, ReferralConfig.MinDepositUsd, default);
        for (var i = 0; i < ReferralConfig.MinActiveDaysInWindow; i++)
            await _f.Qualification.RecordBotTradeActivityAsync(referred.Id, DateTimeOffset.UtcNow.AddDays(-i), default);

        var q = await _f.Qualification.GetAsync(referred.Id, default);
        q!.Qualified.Should().BeTrue();
        q.QualifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Admin_override_forces_the_deposit_condition_regardless_of_observed_balance()
    {
        var referrer = await _f.CreateUserAsync();
        var referred = await _f.CreateUserAsync();
        var q = await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow.AddDays(-20));
        q.AdminDepositOverride = true;
        await _f.Referrals.UpdateQualificationAsync(q);

        for (var i = 0; i < ReferralConfig.MinActiveDaysInWindow; i++)
            await _f.Qualification.RecordBotTradeActivityAsync(referred.Id, DateTimeOffset.UtcNow.AddDays(-i), default);

        var updated = await _f.Qualification.GetAsync(referred.Id, default);
        updated!.DepositMet.Should().BeFalse("balance was never observed");
        updated.Qualified.Should().BeTrue("the admin override stands in for the deposit signal");
    }

    [Fact]
    public async Task Same_day_trades_count_once_toward_active_days()
    {
        var referrer = await _f.CreateUserAsync();
        var referred = await _f.CreateUserAsync();
        await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow.AddDays(-20));

        // Anchored to a fixed hour, not UtcNow: the second trade is +2h, so running this
        // after 22:00 UTC pushed it into the next UTC day and counted two active days.
        // The rule under test is "same UTC day counts once", so the day must be pinned.
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddHours(10);
        await _f.Qualification.RecordBotTradeActivityAsync(referred.Id, today, default);
        await _f.Qualification.RecordBotTradeActivityAsync(referred.Id, today.AddHours(2), default);

        var q = await _f.Qualification.GetAsync(referred.Id, default);
        q!.ActiveDaysCount.Should().Be(1);
        q.BotTradeCount.Should().Be(2);
    }
}

public sealed class ReferralAccrualServiceTests : IClassFixture<ReferralSystemFixture>
{
    private readonly ReferralSystemFixture _f;
    public ReferralAccrualServiceTests(ReferralSystemFixture f) => _f = f;

    [Fact]
    public async Task Settling_the_same_trade_twice_accrues_commission_once()
    {
        var referrer = await _f.CreateUserAsync();
        await _f.GiveQualifiedReferralsAsync(referrer.Id, 5); // reaches tier 1 (0.15%)

        var referred = await _f.CreateUserAsync();
        await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow.AddDays(-20));
        var q = (await _f.Qualification.GetAsync(referred.Id, default))!;
        q.DepositMet = true;
        q.ActiveDaysCount = ReferralConfig.MinActiveDaysInWindow;
        q.Qualified = true;
        q.QualifiedAt = DateTimeOffset.UtcNow;
        await _f.Referrals.UpdateQualificationAsync(q);

        var trade = _f.BuildRealTrade(referred.Id, 100m, TradeStatus.Profit);

        await _f.Accrual.OnTradeSettledAsync(trade, default);
        await _f.Accrual.OnTradeSettledAsync(trade, default);

        var (items, total) = await _f.Referrals.ListCommissionsAsync(referrer.Id, 1, 50, default);
        total.Should().Be(1);
        items.Single().TradeId.Should().Be(trade.Id);
        items.Single().Amount.Should().Be(0.15m); // 100 * 0.15%
    }

    [Fact]
    public async Task A_demo_account_trade_never_accrues_commission()
    {
        var referrer = await _f.CreateUserAsync();
        await _f.GiveQualifiedReferralsAsync(referrer.Id, 5);

        var referred = await _f.CreateUserAsync();
        var q = await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow.AddDays(-20));
        q.DepositMet = true;
        q.ActiveDaysCount = ReferralConfig.MinActiveDaysInWindow;
        q.Qualified = true;
        await _f.Referrals.UpdateQualificationAsync(q);

        var demoTrade = _f.BuildRealTrade(referred.Id, 100m, TradeStatus.Profit);
        demoTrade.AccountType = BinollaAccountType.Demo;

        await _f.Accrual.OnTradeSettledAsync(demoTrade, default);

        (await _f.Referrals.SumCommissionsAsync(referrer.Id, default)).Should().Be(0m);
    }

    [Fact]
    public async Task An_unqualified_referred_users_trade_still_pays_commission()
    {
        // Commission is immediate by design: a referrer earns as soon as the people they
        // invited are trading, instead of waiting out the 15-day qualification window.
        // Rewards are the part that still waits for full qualification.
        ReferralConfig.CommissionFromQualifiedOnly.Should().BeFalse(
            "commission is paid on activity, not on qualification");

        var referrer = await _f.CreateUserAsync();
        await _f.GiveQualifiedReferralsAsync(referrer.Id, 5);

        var referred = await _f.CreateUserAsync();
        await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow); // window just opened — cannot be qualified yet

        var trade = _f.BuildRealTrade(referred.Id, 100m, TradeStatus.Profit);
        await _f.Accrual.OnTradeSettledAsync(trade, default);

        (await _f.Referrals.SumCommissionsAsync(referrer.Id, default)).Should().Be(0.15m);
    }

    [Fact]
    public async Task The_qualified_only_switch_still_withholds_when_turned_on()
    {
        // The stricter mode remains available; this pins that turning it back on actually
        // changes behaviour rather than being dead configuration.
        var previous = ReferralConfig.CommissionFromQualifiedOnly;
        ReferralConfig.CommissionFromQualifiedOnly = true;
        try
        {
            var referrer = await _f.CreateUserAsync();
            await _f.GiveQualifiedReferralsAsync(referrer.Id, 5);

            var referred = await _f.CreateUserAsync();
            await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow);

            var trade = _f.BuildRealTrade(referred.Id, 100m, TradeStatus.Profit);
            await _f.Accrual.OnTradeSettledAsync(trade, default);

            (await _f.Referrals.SumCommissionsAsync(referrer.Id, default)).Should().Be(0m);
        }
        finally
        {
            ReferralConfig.CommissionFromQualifiedOnly = previous;
        }
    }

    [Fact]
    public async Task A_referrer_below_tier_one_earns_no_commission_even_from_a_qualified_referral()
    {
        var referrer = await _f.CreateUserAsync(); // 0 qualified referrals of their own besides this one
        var referred = await _f.CreateUserAsync();
        var q = await _f.AttachAsync(referrer.Id, referred.Id, DateTimeOffset.UtcNow.AddDays(-20));
        q.DepositMet = true;
        q.ActiveDaysCount = ReferralConfig.MinActiveDaysInWindow;
        q.Qualified = true;
        await _f.Referrals.UpdateQualificationAsync(q);

        var trade = _f.BuildRealTrade(referred.Id, 100m, TradeStatus.Loss);
        await _f.Accrual.OnTradeSettledAsync(trade, default);

        (await _f.Referrals.SumCommissionsAsync(referrer.Id, default)).Should().Be(0m, "one qualified referral is below the 5 required for tier 1");
    }
}

public sealed class ReferralPayoutTests : IClassFixture<ReferralSystemFixture>
{
    private readonly ReferralSystemFixture _f;
    public ReferralPayoutTests(ReferralSystemFixture f) => _f = f;

    [Fact]
    public async Task Requesting_less_than_the_minimum_payout_is_rejected()
    {
        var act = () => _f.App.RequestPayoutAsync(
            new ReferralPayoutRequest(ReferralConfig.MinPayoutUsd - 1m, "bank", "IBAN123"), default);

        var ex = await Assert.ThrowsAsync<ApiException>(() => act());
        ex.Code.Should().Be(ApiErrorCodes.PayoutBelowMinimum);
    }

    [Fact]
    public async Task Requesting_more_than_the_available_balance_is_rejected()
    {
        var act = () => _f.App.RequestPayoutAsync(
            new ReferralPayoutRequest(ReferralConfig.MinPayoutUsd + 1000m, "bank", "IBAN123"), default);

        var ex = await Assert.ThrowsAsync<ApiException>(() => act());
        ex.Code.Should().Be(ApiErrorCodes.PayoutInsufficientBalance);
    }
}

public sealed class ReferralAttachTests : IClassFixture<ReferralSystemFixture>
{
    private readonly ReferralSystemFixture _f;
    public ReferralAttachTests(ReferralSystemFixture f) => _f = f;

    [Fact]
    public async Task An_unknown_referral_code_is_ignored_without_failing_signup()
    {
        var newUser = await _f.CreateUserAsync();

        await _f.App.AttachReferrerAsync(newUser.Id, "NOSUCHCODE", default);

        var reloaded = await _f.Users.GetByIdAsync(newUser.Id, default);
        reloaded!.ReferredByUserId.Should().BeNull();
    }

    [Fact]
    public async Task A_valid_code_attaches_the_referral_exactly_once()
    {
        var referrer = await _f.CreateUserAsync();
        var code = await _f.App.GetOrCreateReferralCodeAsync(referrer.Id, default);
        var newUser = await _f.CreateUserAsync();

        await _f.App.AttachReferrerAsync(newUser.Id, code, default);
        await _f.App.AttachReferrerAsync(newUser.Id, code, default); // must not double-attach

        var reloaded = await _f.Users.GetByIdAsync(newUser.Id, default);
        reloaded!.ReferredByUserId.Should().Be(referrer.Id);

        var q = await _f.Referrals.GetQualificationByReferredUserIdAsync(newUser.Id, default);
        q.Should().NotBeNull();
    }
}
