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

        // If a connect/reauth is already running, wait for it — do not start a competing restore.
        // Do not link wait to the HTTP request CT: FE abort (~8–25s) must not cancel an in-flight connect.
        if (client is BinollaSession connectingSession &&
            connectingSession.Lifecycle is SessionLifecycleState.Connecting or SessionLifecycleState.Reconnecting)
        {
            try
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                    "H107",
                    "BotAccessService.CheckAsync",
                    "wait_connecting_begin",
                    new
                    {
                        lifecycle = connectingSession.Lifecycle.ToString(),
                        requestCtCanBeCanceled = ct.CanBeCanceled
                    });
                // #endregion
                await connectingSession.WaitUntilNotConnectingAsync(waitCts.Token);
            }
            catch
            {
                /* fall through to restore / status evaluation */
            }
            client = _sessions.Get(userId.ToString());
        }

        var connected = client is not null &&
                        client.IsTransportConnected &&
                        client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;

        var accountType = (link?.AccountType ?? BinollaAccountType.Demo).ToString();
        var approvalStatus = (link?.ApprovalStatus ?? AdminApprovalStatus.Pending).ToString();
        var adminApproved = link?.AdminApproved == true;

        // Transport flaps during Connecting must not be treated as a sticky SessionExpired.
        var deadSession = client is not null &&
                          client.Lifecycle is SessionLifecycleState.AuthenticationFailed
                              or SessionLifecycleState.SessionExpired;

        // Cap wait independently of the HTTP request CT. Frontend MARKET_FETCH_MS (~8s) was
        // cancelling restore mid-flight and CheckAsync then reported a false SessionExpired.
        if ((!connected || deadSession) &&
            link is not null &&
            link.Status == BinollaLinkStatus.Connected &&
            link.ApprovalStatus != AdminApprovalStatus.Rejected &&
            !string.IsNullOrWhiteSpace(link.EncryptedSsid))
        {
            try
            {
                if (deadSession)
                    _restorer.ClearAuthFailure(userId);

                using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                    "H106",
                    "BotAccessService.CheckAsync",
                    "restore_begin",
                    new
                    {
                        deadSession,
                        priorLifecycle = client?.Lifecycle.ToString() ?? "None",
                        transportUp = client?.IsTransportConnected == true
                    });
                // #endregion
                connected = await _restorer.TryRestoreUserAsync(userId, restoreCts.Token);
                if (connected)
                {
                    client = _sessions.Get(userId.ToString());
                    link = await _links.GetByUserIdAsync(userId, ct) ?? link;
                    accountType = link.AccountType.ToString();
                    approvalStatus = link.ApprovalStatus.ToString();
                    adminApproved = link.AdminApproved;
                    deadSession = false;
                }
            }
            catch (OperationCanceledException)
            {
                // Independent restore timed out — re-read live state; do not invent SessionExpired.
                client = _sessions.Get(userId.ToString());
                connected = client is not null &&
                            client.IsTransportConnected &&
                            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;
                deadSession = client is not null &&
                              client.Lifecycle is SessionLifecycleState.AuthenticationFailed
                                  or SessionLifecycleState.SessionExpired;
            }
            catch
            {
                connected = client is not null &&
                            client.IsTransportConnected &&
                            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;
            }
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
                lifecycle = client?.Lifecycle.ToString() ?? "None",
                transportUp = client?.IsTransportConnected == true,
                linkStatus = link?.Status.ToString() ?? "None"
            });
        // #endregion

        if (deadSession && !connected)
        {
            return new BotAccessResult(
                BotAccessState.SessionExpired,
                BinollaConnected: false,
                AdminApproved: adminApproved,
                AccountType: accountType,
                ApprovalStatus: approvalStatus);
        }

        if (link is null || link.Status != BinollaLinkStatus.Connected || !connected)
        {
            // After failed restore of an expired SSID, Status may be Disconnected while still linked.
            if (link is not null &&
                link.Status == BinollaLinkStatus.Disconnected &&
                link.ApprovalStatus != AdminApprovalStatus.Rejected &&
                !string.IsNullOrWhiteSpace(link.EncryptedSsid))
            {
                return new BotAccessResult(
                    BotAccessState.SessionExpired,
                    BinollaConnected: false,
                    AdminApproved: adminApproved,
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

        if (link.ApprovalStatus == AdminApprovalStatus.Rejected ||
            (link.ApprovalStatus == AdminApprovalStatus.Pending && !link.AdminApproved))
        {
            if (link.ApprovalStatus == AdminApprovalStatus.Rejected)
            {
                return new BotAccessResult(
                    BotAccessState.NotEligible,
                    BinollaConnected: true,
                    AdminApproved: false,
                    AccountType: accountType,
                    ApprovalStatus: approvalStatus);
            }

            return new BotAccessResult(
                BotAccessState.AdminApprovalRequired,
                BinollaConnected: true,
                AdminApproved: false,
                AccountType: accountType,
                ApprovalStatus: approvalStatus);
        }

        if (link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Approved)
        {
            return new BotAccessResult(
                BotAccessState.Allowed,
                BinollaConnected: true,
                AdminApproved: true,
                AccountType: accountType,
                ApprovalStatus: approvalStatus);
        }

        return new BotAccessResult(
            BotAccessState.AdminApprovalRequired,
            BinollaConnected: true,
            AdminApproved: false,
            AccountType: accountType,
            ApprovalStatus: approvalStatus);
    }
}
