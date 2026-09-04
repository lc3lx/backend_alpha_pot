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
    private readonly IBinollaLinkRepository _links;
    private readonly IJwtTokenService _jwt;
    private readonly IUserPasswordHasher _passwords;
    private readonly ISecretProtector _protector;
    private readonly ICurrentUser _currentUser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthAppService> _logger;

    public AuthAppService(
        ITelegramAuthService telegramAuth,
        IUserRepository users,
        IBinollaLinkRepository links,
        IJwtTokenService jwt,
        IUserPasswordHasher passwords,
        ISecretProtector protector,
        ICurrentUser currentUser,
        IConfiguration configuration,
        ILogger<AuthAppService> logger)
    {
        _telegramAuth = telegramAuth;
        _users = users;
        _links = links;
        _jwt = jwt;
        _passwords = passwords;
        _protector = protector;
        _currentUser = currentUser;
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

    /// <summary>
    /// Attach Telegram Mini App identity to the currently authenticated (usually email/demo) user.
    /// Absorbs empty splash stubs so marketing demos can open the bot afterwards via initData alone.
    /// </summary>
    public async Task<AuthSessionResponse> LinkTelegramAsync(TelegramAuthRequest request, CancellationToken ct)
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

        var user = await _users.GetByIdAsync(_currentUser.UserId, ct)
                   ?? throw new ApiException(ApiErrorCodes.Unauthorized, "User not found.", 401);

        if (user.TelegramUserId == identity.TelegramUserId)
        {
            user.Username = identity.Username ?? user.Username;
            user.FullName = identity.FullName ?? user.FullName;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(user, ct);
            return new AuthSessionResponse(_jwt.CreateToken(user), user.Id.ToString());
        }

        if (user.TelegramUserId is long existing && existing != identity.TelegramUserId)
        {
            throw new ApiException(
                ApiErrorCodes.TelegramTaken,
                "This account is already linked to a different Telegram user.",
                409);
        }

        var other = await _users.GetByTelegramUserIdAsync(identity.TelegramUserId, ct);
        if (other is not null && other.Id != user.Id)
        {
            if (!await IsAbsorbableTelegramStubAsync(other, ct))
            {
                throw new ApiException(
                    ApiErrorCodes.TelegramTaken,
                    "That Telegram user is already linked to another account.",
                    409);
            }

            other.TelegramUserId = null;
            other.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(other, ct);
            _logger.LogInformation(
                "Absorbed Telegram stub {StubUserId} into {TargetUserId} for telegram_user_id {TelegramUserId}",
                other.Id, user.Id, identity.TelegramUserId);
        }

        user.TelegramUserId = identity.TelegramUserId;
        user.Username = identity.Username ?? user.Username;
        user.FullName = identity.FullName ?? user.FullName;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _users.UpdateAsync(user, ct);

        _logger.LogInformation(
            "Linked telegram_user_id {TelegramUserId} to user {UserId} marketingDemo={IsMarketingDemo}",
            identity.TelegramUserId, user.Id, user.IsMarketingDemo);

        return new AuthSessionResponse(_jwt.CreateToken(user), user.Id.ToString());
    }

    public async Task<AuthSessionResponse> RegisterAsync(EmailAuthRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        ValidatePassword(request.Password);

        var existing = await _users.GetByEmailAsync(email, ct);
        if (existing is not null)
            throw new ApiException(ApiErrorCodes.EmailTaken, "An account with this email already exists.", 409);

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = _passwords.Hash(request.Password),
            EncryptedLoginPassword = _protector.Encrypt(request.Password),
            FullName = TrimOrNull(request.FullName),
            Country = TrimOrNull(request.Country),
            Username = NormalizeUsername(request.Username),
            Role = ResolveRole(telegramUserId: null, email),
            CreatedAt = now,
            UpdatedAt = now
        };
        await _users.AddAsync(user, ct);
        _logger.LogInformation("Created website user {UserId} email={Email} role={Role}", user.Id, email, user.Role);

        return new AuthSessionResponse(_jwt.CreateToken(user), user.Id.ToString());
    }

    public async Task<AuthSessionResponse> LoginAsync(EmailAuthRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(request.Password))
            throw new ApiException(ApiErrorCodes.InvalidCredentials, "Invalid email or password.", 401);

        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash) || !_passwords.Verify(user.PasswordHash, request.Password))
            throw new ApiException(ApiErrorCodes.InvalidCredentials, "Invalid email or password.", 401);

        if (user.IsMarketingDemo)
            throw new ApiException(
                ApiErrorCodes.UseDemoLogin,
                "This is a marketing demo account. Sign in at /demo-login.",
                403);

        var desiredRole = ResolveRole(user.TelegramUserId, user.Email);
        var dirty = false;
        if (user.Role != desiredRole)
        {
            user.Role = desiredRole;
            dirty = true;
        }

        try
        {
            user.EncryptedLoginPassword = _protector.Encrypt(request.Password);
            dirty = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store encrypted login password for user {UserId}", user.Id);
        }

        if (dirty)
        {
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(user, ct);
        }

        return new AuthSessionResponse(_jwt.CreateToken(user), user.Id.ToString());
    }

    /// <summary>Email/password for marketing demo accounts only (separate from /login).</summary>
    public async Task<AuthSessionResponse> DemoLoginAsync(EmailAuthRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(request.Password))
            throw new ApiException(ApiErrorCodes.InvalidCredentials, "Invalid email or password.", 401);

        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash) || !_passwords.Verify(user.PasswordHash, request.Password))
            throw new ApiException(ApiErrorCodes.InvalidCredentials, "Invalid email or password.", 401);

        if (!user.IsMarketingDemo)
            throw new ApiException(
                ApiErrorCodes.NotMarketingDemo,
                "This is not a marketing demo account. Use the normal login page.",
                403);

        try
        {
            user.EncryptedLoginPassword = _protector.Encrypt(request.Password);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(user, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store encrypted login password for demo user {UserId}", user.Id);
        }

        return new AuthSessionResponse(_jwt.CreateToken(user), user.Id.ToString());
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        ValidatePassword(request.NewPassword);
        var user = await _users.GetByIdAsync(_currentUser.UserId, ct)
                   ?? throw new ApiException(ApiErrorCodes.Unauthorized, "User not found.", 401);

        if (string.IsNullOrEmpty(user.PasswordHash))
            throw new ApiException(ApiErrorCodes.PasswordNotSet, "This account has no password. Sign in with Telegram, or register with email first.", 400);

        if (!_passwords.Verify(user.PasswordHash, request.CurrentPassword))
            throw new ApiException(ApiErrorCodes.InvalidCredentials, "Current password is incorrect.", 401);

        user.PasswordHash = _passwords.Hash(request.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _users.UpdateAsync(user, ct);
    }

    private async Task<bool> IsAbsorbableTelegramStubAsync(User user, CancellationToken ct)
    {
        if (user.Role == UserRole.Admin || user.IsMarketingDemo)
            return false;
        if (!string.IsNullOrEmpty(user.Email) || !string.IsNullOrEmpty(user.PasswordHash))
            return false;

        var link = await _links.GetByUserIdAsync(user.Id, ct);
        return link is null;
    }

    private UserRole ResolveRole(long? telegramUserId, string? email = null)
    {
        if (telegramUserId is long telegramId)
        {
            var raw = _configuration["Admin:TelegramUserIds"]
                      ?? _configuration["ADMIN_TELEGRAM_USER_IDS"]
                      ?? string.Empty;
            foreach (var id in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(id, out var parsed) && parsed == telegramId)
                    return UserRole.Admin;
            }
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var rawEmails = _configuration["Admin:Emails"]
                            ?? _configuration["ADMIN_EMAILS"]
                            ?? string.Empty;
            foreach (var adminEmail in rawEmails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(adminEmail, email, StringComparison.OrdinalIgnoreCase))
                    return UserRole.Admin;
            }
        }

        return UserRole.User;
    }

    private static string NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(value) || !value.Contains('@') || value.Length > 256)
            throw new ApiException(ApiErrorCodes.ValidationError, "A valid email is required.");
        return value;
    }

    private static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new ApiException(ApiErrorCodes.ValidationError, "Password must be at least 8 characters.");
        if (password.Length > 128)
            throw new ApiException(ApiErrorCodes.ValidationError, "Password is too long.");
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeUsername(string? value)
    {
        var trimmed = TrimOrNull(value);
        if (trimmed is null) return null;
        return trimmed.StartsWith('@') ? trimmed[1..] : trimmed;
    }
}
