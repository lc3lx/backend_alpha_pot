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
    private readonly IMarketingDemoService _demo;

    public MeAppService(
        ICurrentUser currentUser,
        IUserRepository users,
        IBinollaLinkRepository links,
        IBinollaSessionManager sessions,
        IMarketingDemoService demo)
    {
        _currentUser = currentUser;
        _users = users;
        _links = links;
        _sessions = sessions;
        _demo = demo;
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(_currentUser.UserId, ct)
                   ?? throw new Common.ApiException(Common.ApiErrorCodes.Unauthorized, "User not found.", 401);

        if (user.IsMarketingDemo)
        {
            _demo.WarmConfig(user);
            return new MeResponse(
                user.Id.ToString(),
                user.TelegramUserId,
                user.Email,
                !string.IsNullOrEmpty(user.PasswordHash),
                user.Username,
                user.FullName,
                user.Country,
                user.Role.ToString(),
                user.Role == UserRole.Admin,
                _demo.BuildStatus(user.Id),
                IsMarketingDemo: true);
        }

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
                Balance: null,
                Lifecycle: client?.Lifecycle.ToString() ?? "None",
                WebSocketConnected: liveConnected);
        }

        return new MeResponse(
            user.Id.ToString(),
            user.TelegramUserId,
            user.Email,
            !string.IsNullOrEmpty(user.PasswordHash),
            user.Username,
            user.FullName,
            user.Country,
            user.Role.ToString(),
            user.Role == UserRole.Admin,
            binolla,
            IsMarketingDemo: false);
    }

    public async Task<MeResponse> UpdateAsync(UpdateProfileRequest request, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(_currentUser.UserId, ct)
                   ?? throw new Common.ApiException(Common.ApiErrorCodes.Unauthorized, "User not found.", 401);

        if (request.FullName is not null)
            user.FullName = string.IsNullOrWhiteSpace(request.FullName) ? user.FullName : request.FullName.Trim();
        if (request.Country is not null)
            user.Country = string.IsNullOrWhiteSpace(request.Country) ? user.Country : request.Country.Trim();
        if (request.Username is not null)
        {
            var username = request.Username.Trim();
            if (username.StartsWith('@'))
                username = username[1..];
            user.Username = string.IsNullOrWhiteSpace(username) ? user.Username : username;
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _users.UpdateAsync(user, ct);
        return await GetMeAsync(ct);
    }
}
