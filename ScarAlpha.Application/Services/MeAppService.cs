using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

public sealed class MeAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserRepository _users;
    private readonly IBinollaLinkRepository _links;
    private readonly IBinollaSessionManager _sessions;

    public MeAppService(
        ICurrentUser currentUser,
        IUserRepository users,
        IBinollaLinkRepository links,
        IBinollaSessionManager sessions)
    {
        _currentUser = currentUser;
        _users = users;
        _links = links;
        _sessions = sessions;
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(_currentUser.UserId, ct)
                   ?? throw new Common.ApiException(Common.ApiErrorCodes.Unauthorized, "User not found.", 401);

        var link = await _links.GetByUserIdAsync(user.Id, ct);
        var client = _sessions.Get(user.Id.ToString());
        var liveConnected = client is not null &&
                            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;

        BinollaStatusDto? binolla = null;
        if (link is not null || liveConnected)
        {
            binolla = new BinollaStatusDto(
                Connected: liveConnected,
                AccountType: (link?.AccountType ?? BinollaAccountType.Demo).ToString(),
                Status: liveConnected
                    ? nameof(BinollaLinkStatus.Connected)
                    : (link?.Status.ToString() ?? nameof(BinollaLinkStatus.Disconnected)),
                LastConnectedAt: link?.LastConnectedAt,
                Balance: null);
        }

        return new MeResponse(
            user.Id.ToString(),
            user.TelegramUserId,
            user.Username,
            user.FullName,
            user.Country,
            user.Role.ToString(),
            user.Role == UserRole.Admin,
            binolla);
    }
}
