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
    private readonly IJwtTokenService _jwt;
    private readonly BinollaAppService _binolla;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BinollaAuthAppService> _logger;

    public BinollaAuthAppService(
        IUserRepository users,
        IJwtTokenService jwt,
        BinollaAppService binolla,
        IConfiguration configuration,
        ILogger<BinollaAuthAppService> logger)
    {
        _users = users;
        _jwt = jwt;
        _binolla = binolla;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<BinollaAuthResponse> LoginAsync(BinollaCredentialRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null)
        {
            throw new ApiException(
                ApiErrorCodes.InvalidCredentials,
                "No Scar Alpha profile for this Binolla email. Sign up on Binolla first.",
                401);
        }

        if (user.IsMarketingDemo)
        {
            throw new ApiException(
                ApiErrorCodes.UseDemoLogin,
                "This is a marketing demo account. Sign in at /demo-login.",
                403);
        }

        var connect = await _binolla.LoginWithCredentialsForUserAsync(user.Id, request, ct);
        var token = _jwt.CreateToken(user);
        _logger.LogInformation("Binolla web login user={UserId} email={Email}", user.Id, email);
        return ToAuthResponse(token, user.Id.ToString(), connect);
    }

    public async Task<BinollaAuthResponse> SignUpAsync(BinollaCredentialRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var existing = await _users.GetByEmailAsync(email, ct);
        if (existing is not null)
        {
            throw new ApiException(
                ApiErrorCodes.EmailTaken,
                "An account with this Binolla email already exists. Log in instead.",
                409);
        }

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
        _logger.LogInformation("Created Binolla web user {UserId} email={Email}", user.Id, email);

        var connect = await _binolla.SignUpWithCredentialsForUserAsync(user.Id, request, ct);
        var token = _jwt.CreateToken(user);
        return ToAuthResponse(token, user.Id.ToString(), connect);
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
