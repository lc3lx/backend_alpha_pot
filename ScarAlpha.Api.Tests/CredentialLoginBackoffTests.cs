using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Infrastructure.Workers;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// A broker refusing logins must not be retried at full speed.
///
/// <para>The bot worker calls the credential relogin on every tick while a session is
/// down — once a second. The relogin used to clear the failure cooldown before each
/// attempt, so a Binolla <c>403</c> produced a fresh headless-browser login every few
/// seconds, each taking 20-40s and overlapping the last. That is how a recoverable
/// outage turns into a rate-limit ban on the server's IP.</para>
/// </summary>
public sealed class CredentialLoginBackoffTests
{
    private static BinollaSessionRestoreService NewService(int cooldownSeconds = 30)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        // Only the throttling state is under test here; the broker and crypto collaborators
        // are never reached by CanAttemptCredentialLogin / MarkCredentialLoginFailed.
        return new BinollaSessionRestoreService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new Mock<IBinollaSessionManager>(MockBehavior.Loose).Object,
            new Mock<IBrokerSessionManager>(MockBehavior.Loose).Object,
            new Mock<ISecretProtector>(MockBehavior.Loose).Object,
            Options.Create(new BinollaSessionRestoreOptions
            {
                Enabled = true,
                FailureCooldownSeconds = cooldownSeconds
            }),
            NullLogger<BinollaSessionRestoreService>.Instance);
    }

    [Fact]
    public void A_first_attempt_is_allowed()
    {
        var svc = NewService();
        svc.CanAttemptCredentialLogin(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void A_second_attempt_is_refused_while_one_is_in_flight()
    {
        var svc = NewService();
        var user = Guid.NewGuid();

        svc.CanAttemptCredentialLogin(user).Should().BeTrue();

        // The worker ticks again a second later while the browser is still running.
        // Allowing this is what stacked overlapping logins.
        svc.CanAttemptCredentialLogin(user).Should().BeFalse();
    }

    [Fact]
    public void A_failure_blocks_the_next_attempt()
    {
        var svc = NewService();
        var user = Guid.NewGuid();

        svc.CanAttemptCredentialLogin(user).Should().BeTrue();
        svc.MarkCredentialLoginFailed(user);

        svc.CanAttemptCredentialLogin(user).Should().BeFalse("the cooldown has just started");
    }

    [Fact]
    public void Success_clears_the_backoff_so_the_next_outage_retries_promptly()
    {
        var svc = NewService();
        var user = Guid.NewGuid();

        svc.CanAttemptCredentialLogin(user).Should().BeTrue();
        svc.MarkCredentialLoginFailed(user);
        svc.CanAttemptCredentialLogin(user).Should().BeFalse();

        // A real login resets everything — the in-flight slot and the cooldown.
        svc.ClearAuthFailure(user);

        svc.CanAttemptCredentialLogin(user).Should().BeTrue();
    }

    [Fact]
    public void Only_one_capture_runs_at_a_time_across_all_users()
    {
        var svc = NewService();

        // A capture drives a headless browser and hits Binolla from the server's single
        // public IP. Five bots with dead sessions each holding their own 30s cooldown
        // still produced an attempt every ~6 seconds, which is what the broker blocks.
        svc.CanAttemptCredentialLogin(Guid.NewGuid()).Should().BeTrue();
        svc.CanAttemptCredentialLogin(Guid.NewGuid()).Should().BeFalse();
        svc.CanAttemptCredentialLogin(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void An_account_failure_leaves_other_users_free_after_the_slot_is_released()
    {
        var svc = NewService(cooldownSeconds: 30);
        var failing = Guid.NewGuid();

        svc.CanAttemptCredentialLogin(failing).Should().BeTrue();

        // Account-level failure: the global slot is released, but the per-user cooldown
        // keeps THIS user out.
        svc.MarkCredentialLoginFailed(failing, blockedByBroker: false);

        svc.CanAttemptCredentialLogin(failing).Should().BeFalse("this user is cooling down");
    }

    [Fact]
    public void A_broker_ip_block_pauses_every_user()
    {
        var svc = NewService();
        var first = Guid.NewGuid();

        svc.CanAttemptCredentialLogin(first).Should().BeTrue();

        // HTTP 403 is the broker refusing the SERVER. Retrying for a different account
        // cannot succeed and only deepens the block.
        svc.MarkCredentialLoginFailed(first, blockedByBroker: true);

        svc.CanAttemptCredentialLogin(Guid.NewGuid())
            .Should().BeFalse("the block is IP-wide, not per-account");
    }

    [Fact]
    public void A_successful_capture_clears_the_global_pause()
    {
        var svc = NewService();
        var blocked = Guid.NewGuid();

        svc.CanAttemptCredentialLogin(blocked).Should().BeTrue();
        svc.MarkCredentialLoginFailed(blocked, blockedByBroker: true);
        svc.CanAttemptCredentialLogin(Guid.NewGuid()).Should().BeFalse();

        // A capture that works proves the IP is acceptable again.
        svc.ClearAuthFailure(blocked);

        svc.CanAttemptCredentialLogin(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void A_zero_cooldown_setting_still_backs_off()
    {
        // Guards against a misconfiguration reintroducing the hammering loop.
        var svc = NewService(cooldownSeconds: 0);
        var user = Guid.NewGuid();

        svc.CanAttemptCredentialLogin(user).Should().BeTrue();
        svc.MarkCredentialLoginFailed(user);

        svc.CanAttemptCredentialLogin(user).Should().BeFalse();
    }
}
