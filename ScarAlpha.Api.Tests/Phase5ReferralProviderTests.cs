using FluentAssertions;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Access;
using Moq;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class Phase5AccessUnitTests
{
    [Fact]
    public async Task Pending_link_yields_AdminApprovalRequired()
    {
        var userId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var link = new BinollaLink
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EncryptedSsid = "encrypted",
            AccountType = BinollaAccountType.Demo,
            Status = BinollaLinkStatus.Connected,
            AdminApproved = false,
            ApprovalStatus = AdminApprovalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var links = new Mock<IBinollaLinkRepository>(MockBehavior.Strict);
        links.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(link);

        var client = new Mock<IBrokerClient>(MockBehavior.Strict);
        client.SetupGet(c => c.Lifecycle).Returns(SessionLifecycleState.Connected);
        client.SetupGet(c => c.IsTransportConnected).Returns(true);

        var sessions = new Mock<IBrokerSessionManager>(MockBehavior.Strict);
        sessions.Setup(s => s.Get(userId, Brokers.Binolla)).Returns(client.Object);

        var restorer = new Mock<IBinollaSessionRestorer>(MockBehavior.Strict);
        restorer.Setup(r => r.TryRestoreUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        restorer.Setup(r => r.EnsureBackgroundRestore(It.IsAny<Guid>()));
        restorer.Setup(r => r.ClearAuthFailure(It.IsAny<Guid>()));

        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        users.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                Id = userId,
                Role = UserRole.User,
                IsMarketingDemo = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

        var botAccess = new BotAccessService(links.Object, sessions.Object, restorer.Object, users.Object);
        var result = await botAccess.CheckAsync(userId);

        result.Access.Should().Be(BotAccessState.AdminApprovalRequired);
        result.AdminApproved.Should().BeFalse();
    }

    /// <summary>
    /// A Quotex link has no SSID — the gateway logs in with the stored credential pair — so
    /// an access check that tests for an SSID reads a perfectly good account as unlinked and
    /// bounces the user to the login screen on every page.
    /// </summary>
    [Fact]
    public async Task Approved_quotex_link_without_an_ssid_is_still_allowed()
    {
        var userId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var link = new BinollaLink
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Broker = Brokers.Quotex,
            EncryptedSsid = null,
            EncryptedBinollaEmail = "encrypted-email",
            EncryptedBinollaPassword = "encrypted-password",
            AccountType = BinollaAccountType.Real,
            Status = BinollaLinkStatus.Connected,
            AdminApproved = true,
            ApprovalStatus = AdminApprovalStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var links = new Mock<IBinollaLinkRepository>(MockBehavior.Strict);
        links.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(link);

        var client = new Mock<IBrokerClient>(MockBehavior.Strict);
        client.SetupGet(c => c.Lifecycle).Returns(SessionLifecycleState.Connected);
        client.SetupGet(c => c.IsTransportConnected).Returns(true);

        // Asked for on Quotex, never on Binolla: routing the lookup to the wrong venue
        // returns no session, which is the failure this pins.
        var sessions = new Mock<IBrokerSessionManager>(MockBehavior.Strict);
        sessions.Setup(s => s.Get(userId, Brokers.Quotex)).Returns(client.Object);

        var restorer = new Mock<IBinollaSessionRestorer>(MockBehavior.Strict);
        restorer.Setup(r => r.EnsureBackgroundRestore(It.IsAny<Guid>()));

        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        users.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                Id = userId,
                Role = UserRole.User,
                IsMarketingDemo = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

        var botAccess = new BotAccessService(links.Object, sessions.Object, restorer.Object, users.Object);
        var result = await botAccess.CheckAsync(userId);

        result.Access.Should().Be(BotAccessState.Allowed);
        result.BinollaConnected.Should().BeTrue();
    }
}

