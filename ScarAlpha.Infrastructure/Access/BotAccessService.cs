using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
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

        var connected = client is not null &&
                        client.IsTransportConnected &&
                        client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;

        var accountType = (link?.AccountType ?? BinollaAccountType.Demo).ToString();
        var approvalStatus = (link?.ApprovalStatus ?? AdminApprovalStatus.Pending).ToString();
        var adminApproved = link?.AdminApproved == true;

        var deadSession = client is not null &&
                          (client.Lifecycle is SessionLifecycleState.AuthenticationFailed
                              or SessionLifecycleState.SessionExpired
                           || !client.IsTransportConnected);

        // Approved/pending + Connected-in-DB but no live session (API restart / idle eviction / unauthorized):
        // best-effort lazy restore before reporting disconnect / session expired.
        // Cap wait — never block /api/account/status for the full WS auth timeout (~20s).
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

                using var restoreCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                restoreCts.CancelAfter(TimeSpan.FromSeconds(4));
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
            catch
            {
                connected = client is not null &&
                            client.IsTransportConnected &&
                            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;
            }
        }

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
