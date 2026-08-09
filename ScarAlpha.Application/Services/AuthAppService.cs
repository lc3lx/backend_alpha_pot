using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

public sealed class AuthAppService
{
    private readonly ITelegramAuthService _telegramAuth;
    private readonly IUserRepository _users;
    private readonly IJwtTokenService _jwt;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthAppService> _logger;

    public AuthAppService(
        ITelegramAuthService telegramAuth,
        IUserRepository users,
        IJwtTokenService jwt,
        IConfiguration configuration,
        ILogger<AuthAppService> logger)
    {
        _telegramAuth = telegramAuth;
        _users = users;
        _jwt = jwt;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AuthSessionResponse> AuthenticateTelegramAsync(TelegramAuthRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InitData))
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "initData is required.", 401);

        TelegramAuthResult identity;
        try
        {
            identity = _telegramAuth.ValidateInitData(request.InitData);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "Telegram authentication failed.", 401);
        }

        var user = await _users.GetByTelegramUserIdAsync(identity.TelegramUserId, ct);
        var now = DateTimeOffset.UtcNow;
        var desiredRole = ResolveRole(identity.TelegramUserId);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                TelegramUserId = identity.TelegramUserId,
                Username = identity.Username,
                FullName = identity.FullName,
                Role = desiredRole,
                CreatedAt = now,
                UpdatedAt = now
            };
            await _users.AddAsync(user, ct);
            _logger.LogInformation("Created user for telegram_user_id {TelegramUserId} role={Role}",
                identity.TelegramUserId, user.Role);
        }
        else
        {
            user.Username = identity.Username ?? user.Username;
            user.FullName = identity.FullName ?? user.FullName;
            // Config is the source of truth: promote AND demote on each login.
            if (user.Role != desiredRole)
            {
                _logger.LogInformation(
                    "Role sync telegram_user_id {TelegramUserId} {From} → {To}",
                    identity.TelegramUserId, user.Role, desiredRole);
                user.Role = desiredRole;
            }
            user.UpdatedAt = now;
            await _users.UpdateAsync(user, ct);
        }

        var token = _jwt.CreateToken(user);
        return new AuthSessionResponse(token, user.Id.ToString());
    }

    private UserRole ResolveRole(long telegramUserId)
    {
        var raw = _configuration["Admin:TelegramUserIds"]
                  ?? _configuration["ADMIN_TELEGRAM_USER_IDS"]
                  ?? string.Empty;
        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids)
        {
            if (long.TryParse(id, out var parsed) && parsed == telegramUserId)
                return UserRole.Admin;
        }

        return UserRole.User;
    }
}
