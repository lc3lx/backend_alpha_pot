using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Access;

public sealed class BotAccessService : IBotAccessService
{
    private readonly IBinollaLinkRepository _links;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBinollaSessionRestorer _restorer;

    public BotAccessService(
        IBinollaLinkRepository links,
        IBinollaSessionManager sessions,
        IBinollaSessionRestorer restorer)
    {
        _links = links;
        _sessions = sessions;
        _restorer = restorer;
    }

    public async Task<BotAccessResult> CheckAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await _links.GetByUserIdAsync(userId, ct);
        var client = _sessions.Get(userId.ToString());

        // Brief wait only if a connect is already in flight — never start a competing 35s restore
        // on every page's /api/account/status (PM2: status 6–9s + assets 30s per navigation).
        if (client is BinollaSession connectingSession &&
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

            client = _sessions.Get(userId.ToString());
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
                        !string.IsNullOrWhiteSpace(link.EncryptedSsid);

        // Non-blocking: keep the socket warm in the background. Status must stay fast.
        if (linkReady && (!connected || deadSession))
        {
            if (deadSession)
                _restorer.ClearAuthFailure(userId);
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
            !string.IsNullOrWhiteSpace(link.EncryptedSsid) &&
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

    private static bool IsLive(IBinollaClient? client) =>
        client is not null &&
        client.IsTransportConnected &&
        client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;
}
