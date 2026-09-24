using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Workers;

namespace ScarAlpha.Api.Tests;

public sealed class QuotexMultiAccountRestoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Restore_restores_missing_quotex_accounts(bool firstAlreadyLive, bool interactive)
    {
        var links = Enumerable.Range(0, 3).Select(_ => new BinollaLink
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Broker = Brokers.Quotex,
            Status = BinollaLinkStatus.Connected, ApprovalStatus = AdminApprovalStatus.Approved,
            AdminApproved = true, EncryptedSsid = "encrypted-test-session"
        }).ToList();
        var repository = new Mock<IBinollaLinkRepository>();
        repository.Setup(x => x.ListAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(links);
        repository.Setup(x => x.GetByUserIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => links.Single(x => x.UserId == id));
        repository.Setup(x => x.UpsertAsync(It.IsAny<BinollaLink>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var live = new Dictionary<Guid, IBrokerClient>();
        IBrokerClient ConnectedClient()
        {
            var client = new Mock<IBrokerClient>();
            client.SetupGet(x => x.IsTransportConnected).Returns(true);
            client.SetupGet(x => x.Lifecycle).Returns(SessionLifecycleState.Connected);
            return client.Object;
        }
        if (firstAlreadyLive) live[links[0].UserId] = ConnectedClient();
        var gateway = new Mock<IBrokerSessionManager>();
        gateway.Setup(x => x.Get(It.IsAny<Guid>(), Brokers.Quotex))
            .Returns((Guid id, string _) => live.GetValueOrDefault(id));
        gateway.Setup(x => x.IsGatewayReachableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        gateway.Setup(x => x.GatewaySessionCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => live.Count);
        gateway.Setup(x => x.RefreshGatewaySessionsAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        gateway.Setup(x => x.GetOrCreateAsync(It.IsAny<Guid>(), Brokers.Quotex,
                It.IsAny<BrokerCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string _, BrokerCredentials credentials, CancellationToken ct) =>
            {
                Assert.Equal("decrypted-test-session", credentials.Ssid);
                return live[id] = ConnectedClient();
            });
        var protector = new Mock<ISecretProtector>();
        protector.Setup(x => x.Decrypt("encrypted-test-session")).Returns("decrypted-test-session");
        using var services = new ServiceCollection().AddSingleton(repository.Object).BuildServiceProvider();
        var restorer = new BinollaSessionRestoreService(
            services.GetRequiredService<IServiceScopeFactory>(), Mock.Of<IBinollaSessionManager>(),
            gateway.Object, protector.Object, Options.Create(new BinollaSessionRestoreOptions { MaxAttempts = 1 }),
            NullLogger<BinollaSessionRestoreService>.Instance);

        if (interactive)
            Assert.True(await restorer.EnsureUserLiveAsync(links[2].UserId, waitForRestore: true));
        else
            await restorer.RestoreApprovedSessionsAsync();

        Assert.Equal(interactive ? 2 : 3, live.Count);
        gateway.Verify(x => x.GetOrCreateAsync(It.IsAny<Guid>(), Brokers.Quotex,
            It.IsAny<BrokerCredentials>(), It.IsAny<CancellationToken>()), Times.Exactly(interactive ? 1 : firstAlreadyLive ? 2 : 3));
    }
}
