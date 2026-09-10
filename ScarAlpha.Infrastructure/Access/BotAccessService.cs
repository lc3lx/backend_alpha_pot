using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.BrokerGateway;

namespace ScarAlpha.Infrastructure.Access;

public sealed class BotAccessService : IBotAccessService
{
    private static readonly ConcurrentDictionary<Guid, CacheEntry> AccessCache = new();
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> UserGates = new();

    private readonly IBinollaLinkRepository _links;
    private readonly IBrokerSessionManager _sessions;
    private readonly IBinollaSessionRestorer _restorer;
    private readonly IUserRepository _users;

    public BotAccessService(
        IBinollaLinkRepository links,
        IBrokerSessionManager sessions,
        IBinollaSessionRestorer restorer,
        IUserRepository users)
    {
        _links = links;
        _sessions = sessions;
        _restorer = restorer;
        _users = users;
    }

    public async Task<BotAccessResult> CheckAsync(Guid userId, CancellationToken ct = default)
    {
        if (TryGetCached(userId, out var cached))
            return cached;

        var gate = UserGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCached(userId, out cached))
                return cached;

            var result = await CheckUncachedAsync(userId, ct).ConfigureAwait(false);
            AccessCache[userId] = new CacheEntry(DateTimeOffset.UtcNow, result);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(Guid userId) => AccessCache.TryRemove(userId, out _);

    private static bool TryGetCached(Guid userId, out BotAccessResult result)
    {
        if (AccessCache.TryGetValue(userId, out var hit) &&
            DateTimeOffset.UtcNow - hit.At < TimeSpan.FromSeconds(2))
        {
            result = hit.Result;
            return true;
        }

        result = default!;
        return false;
    }

    private async Task<BotAccessResult> CheckUncachedAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user?.IsMarketingDemo == true)
        {
            return new BotAccessResult(
                BotAccessState.Allowed,
                BinollaConnected: true,
                AdminApproved: true,
                AccountType: BinollaAccountType.Demo.ToString(),
                ApprovalStatus: AdminApprovalStatus.Approved.ToString());
        }

        var link = await _links.GetByUserIdAsync(userId, ct);
        // The link is the record of which venue this user chose, so it answers the routing
        // question directly — no resolver lookup, and no second read of the same row.
        var broker = Brokers.Normalize(link?.Broker);
        var client = _sessions.Get(userId, broker);

        // Brief wait only if a connect is already in flight — never start a competing 35s restore
        // on every page's /api/account/status (PM2: status 6–9s + assets 30s per navigation).
        if (client is BinollaBrokerAdapter { Inner: BinollaSession connectingSession } &&
            connectingSession.Lifecycle is SessionLifecycleState.Connecting or SessionLifecycleState.Reconnecting)
        {
            try
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await connectingSession.WaitUntilNotConnectingAsync(waitCts.Token);
            }
            catch
            {
                /* fall through */
            }

            client = _sessions.Get(userId, broker);
        }

        var connected = IsLive(client);
        var accountType = (link?.AccountType ?? BinollaAccountType.Demo).ToString();
        var approvalStatus = (link?.ApprovalStatus ?? AdminApprovalStatus.Pending).ToString();
        var adminApproved = link?.AdminApproved == true;

        var deadSession = client is not null &&
                          client.Lifecycle is SessionLifecycleState.AuthenticationFailed
                              or SessionLifecycleState.SessionExpired;

        var linkReady = link is not null &&
                        link.Status == BinollaLinkStatus.Connected &&
                        link.ApprovalStatus != AdminApprovalStatus.Rejected &&
                        HasReconnectSecret(link, broker);

        // Non-blocking: keep the socket warm in the background. Status must stay fast.
        if (linkReady && (!connected || deadSession))
        {
            _restorer.EnsureBackgroundRestore(userId);
        }

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
            "H106",
            "BotAccessService.CheckAsync",
            "access_result",
            new
            {
                connected,
                deadSession,
                linkReady,
                lifecycle = client?.Lifecycle.ToString() ?? "None",
                transportUp = client?.IsTransportConnected == true,
                linkStatus = link?.Status.ToString() ?? "None",
                blockingRestore = false
            });
        // #endregion

        // Sticky auth failure + no live socket → SessionExpired (user must re-login).
        if (deadSession && !connected && !linkReady)
        {
            return new BotAccessResult(
                BotAccessState.SessionExpired,
                BinollaConnected: false,
                AdminApproved: adminApproved,
                AccountType: accountType,
                ApprovalStatus: approvalStatus);
        }

        if (link is null)
        {
            return new BotAccessResult(
                BotAccessState.BinollaNotConnected,
                BinollaConnected: false,
                AdminApproved: false,
                AccountType: accountType,
                ApprovalStatus: approvalStatus);
        }

        if (link.Status == BinollaLinkStatus.Disconnected &&
            link.ApprovalStatus != AdminApprovalStatus.Rejected &&
            HasReconnectSecret(link, broker) &&
            !connected)
        {
            return new BotAccessResult(
                BotAccessState.SessionExpired,
                BinollaConnected: false,
                AdminApproved: adminApproved,
                AccountType: accountType,
                ApprovalStatus: approvalStatus);
        }

        // Link still Connected in DB: keep the user inside the bot while WS restores.
        // Previously !connected forced BinollaNotConnected → FE bounced to login on every page.
        if (linkReady || connected)
        {
            if (link.ApprovalStatus == AdminApprovalStatus.Rejected)
            {
                return new BotAccessResult(
                    BotAccessState.NotEligible,
                    BinollaConnected: connected,
                    AdminApproved: false,
                    AccountType: accountType,
                    ApprovalStatus: approvalStatus);
            }

            if (link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Approved)
            {
                return new BotAccessResult(
                    BotAccessState.Allowed,
                    BinollaConnected: connected,
                    AdminApproved: true,
                    AccountType: accountType,
                    ApprovalStatus: approvalStatus);
            }

            return new BotAccessResult(
                BotAccessState.AdminApprovalRequired,
                BinollaConnected: connected,
                AdminApproved: false,
                AccountType: accountType,
                ApprovalStatus: approvalStatus);
        }

        return new BotAccessResult(
            BotAccessState.BinollaNotConnected,
            BinollaConnected: connected,
            AdminApproved: adminApproved,
            AccountType: accountType,
            ApprovalStatus: approvalStatus);
    }

    private readonly record struct CacheEntry(DateTimeOffset At, BotAccessResult Result);

    /// <summary>
    /// Whether the link holds enough to get back onto the broker without the user typing
    /// anything again.
    ///
    /// <para>Which secret that is differs by venue: Binolla reconnects on a captured SSID,
    /// while a gateway broker logs in with the stored credential pair. Testing only for an
    /// SSID — as this did — reads a perfectly usable Quotex link as unconnectable and drops
    /// the user back to the login screen.</para>
    /// </summary>
    private static bool HasReconnectSecret(Domain.Entities.BinollaLink link, string broker) =>
        broker == Brokers.Binolla
            ? !string.IsNullOrWhiteSpace(link.EncryptedSsid)
            : !string.IsNullOrWhiteSpace(link.EncryptedBinollaEmail)
              && !string.IsNullOrWhiteSpace(link.EncryptedBinollaPassword);

    private static bool IsLive(IBrokerClient? client) =>
        client is not null &&
        client.IsTransportConnected &&
        client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;
}
