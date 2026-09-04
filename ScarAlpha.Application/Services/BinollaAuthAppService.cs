using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

/// <summary>
/// Public Binolla-only authentication for web clients — no separate Scar Alpha password login.
/// </summary>
public sealed class BinollaAuthAppService
{
    private readonly IUserRepository _users;
    private readonly IBinollaLinkRepository _links;
    private readonly ISecretProtector _protector;
    private readonly IJwtTokenService _jwt;
    private readonly BinollaAppService _binolla;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BinollaAuthAppService> _logger;

    public BinollaAuthAppService(
        IUserRepository users,
        IBinollaLinkRepository links,
        ISecretProtector protector,
        IJwtTokenService jwt,
        BinollaAppService binolla,
        IConfiguration configuration,
        ILogger<BinollaAuthAppService> logger)
    {
        _users = users;
        _links = links;
        _protector = protector;
        _jwt = jwt;
        _binolla = binolla;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<BinollaAuthResponse> LoginAsync(BinollaCredentialRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = await ResolveUserForBinollaEmailAsync(email, ct);
        var provisioned = false;

        if (user is null)
        {
            user = await ProvisionUserForBinollaLoginAsync(email, ct);
            provisioned = true;
        }

        if (user.IsMarketingDemo)
        {
            throw new ApiException(
                ApiErrorCodes.UseDemoLogin,
                "This is a marketing demo account. Sign in at /demo-login.",
                403);
        }

        await EnsureUserEmailAsync(user, email, ct);

        var connect = await _binolla.LoginWithCredentialsForUserAsync(user.Id, request, ct);
        try
        {
            user.EncryptedLoginPassword = _protector.Encrypt(request.Password);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(user, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store encrypted login password for Binolla user {UserId}", user.Id);
        }

        var token = _jwt.CreateToken(user);
        _logger.LogInformation(
            "Binolla web login user={UserId} email={Email} provisioned={Provisioned} telegram={TelegramId}",
            user.Id, email, provisioned, user.TelegramUserId);
        return ToAuthResponse(token, user.Id.ToString(), connect);
    }

    public async Task<BinollaAuthResponse> SignUpAsync(BinollaCredentialRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var existing = await ResolveUserForBinollaEmailAsync(email, ct);
        if (existing is not null)
        {
            throw new ApiException(
                ApiErrorCodes.EmailTaken,
                "An account with this Binolla email already exists. Log in instead.",
                409);
        }

        var user = await ProvisionUserForBinollaLoginAsync(email, ct);
        try
        {
            user.EncryptedLoginPassword = _protector.Encrypt(request.Password);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(user, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store encrypted login password for new Binolla user {UserId}", user.Id);
        }

        var connect = await _binolla.SignUpWithCredentialsForUserAsync(user.Id, request, ct);
        var token = _jwt.CreateToken(user);
        return ToAuthResponse(token, user.Id.ToString(), connect);
    }

    /// <summary>
    /// Website email match, then stored Binolla email on an existing bot/Telegram link.
    /// </summary>
    private async Task<User?> ResolveUserForBinollaEmailAsync(string email, CancellationToken ct)
    {
        var byEmail = await _users.GetByEmailAsync(email, ct);
        if (byEmail is not null)
            return byEmail;

        return await FindUserByStoredBinollaEmailAsync(email, ct);
    }

    private async Task<User?> FindUserByStoredBinollaEmailAsync(string email, CancellationToken ct)
    {
        var links = await _links.ListWithStoredBinollaEmailAsync(ct);
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.EncryptedBinollaEmail))
                continue;

            try
            {
                var stored = _protector.Decrypt(link.EncryptedBinollaEmail).Trim();
                if (!string.Equals(stored, email, StringComparison.OrdinalIgnoreCase))
                    continue;

                var user = await _users.GetByIdAsync(link.UserId, ct);
                if (user is not null)
                {
                    _logger.LogInformation(
                        "Resolved web login via stored Binolla email user={UserId} telegram={TelegramId}",
                        user.Id, user.TelegramUserId);
                    return user;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping corrupt Binolla email blob for link {LinkId}", link.Id);
            }
        }

        return null;
    }

    private async Task<User> ProvisionUserForBinollaLoginAsync(string email, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Role = ResolveRole(email),
            CreatedAt = now,
            UpdatedAt = now
        };
        await _users.AddAsync(user, ct);
        _logger.LogInformation("Provisioned web user from Binolla login {UserId} email={Email}", user.Id, email);
        return user;
    }

    private async Task EnsureUserEmailAsync(User user, string email, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(user.Email))
            return;

        user.Email = email;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _users.UpdateAsync(user, ct);
        _logger.LogInformation("Backfilled email on user {UserId} from Binolla login", user.Id);
    }

    private static BinollaAuthResponse ToAuthResponse(
        string accessToken,
        string userId,
        BinollaConnectResponse connect) =>
        new(
            accessToken,
            userId,
            connect.Connected,
            connect.AccountType,
            connect.Access,
            connect.AdminApproved,
            connect.ApprovalStatus,
            connect.LastConnectedAt,
            connect.Balance);

    private UserRole ResolveRole(string email)
    {
        var rawEmails = _configuration["Admin:Emails"]
                        ?? _configuration["ADMIN_EMAILS"]
                        ?? string.Empty;
        foreach (var adminEmail in rawEmails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(adminEmail, email, StringComparison.OrdinalIgnoreCase))
                return UserRole.Admin;
        }

        return UserRole.User;
    }

    private static string NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(value) || !value.Contains('@') || value.Length > 256)
            throw new ApiException(ApiErrorCodes.ValidationError, "A valid Binolla email is required.");
        return value;
    }
}
