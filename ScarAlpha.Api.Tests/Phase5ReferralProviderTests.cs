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

        var client = new Mock<IBinollaClient>(MockBehavior.Strict);
        client.SetupGet(c => c.Lifecycle).Returns(SessionLifecycleState.Connected);
        client.SetupGet(c => c.IsTransportConnected).Returns(true);

        var sessions = new Mock<IBinollaSessionManager>(MockBehavior.Strict);
        sessions.Setup(s => s.Get(userId.ToString())).Returns(client.Object);

        var restorer = new Mock<IBinollaSessionRestorer>(MockBehavior.Strict);
        restorer.Setup(r => r.TryRestoreUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        restorer.Setup(r => r.EnsureBackgroundRestore(It.IsAny<Guid>()));
        restorer.Setup(r => r.ClearAuthFailure(It.IsAny<Guid>()));

        var botAccess = new BotAccessService(links.Object, sessions.Object, restorer.Object);
        var result = await botAccess.CheckAsync(userId);

        result.Access.Should().Be(BotAccessState.AdminApprovalRequired);
        result.AdminApproved.Should().BeFalse();
    }
}

