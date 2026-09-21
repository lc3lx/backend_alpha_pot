using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;
using DomainAccount = ScarAlpha.Domain.Enums.BinollaAccountType;
using EngineAccount = ScarAlpha.Binolla.Models.AccountType;

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
    private readonly ReferralAppService _referrals;
    private readonly IBrokerSessionManager _brokers;
    private readonly IBotAccessService _access;
    private readonly IBinollaSessionRestorer _restorer;
    private readonly IUserPasswordHasher _passwords;
    private readonly ILogger<BinollaAuthAppService> _logger;

    public BinollaAuthAppService(
        IUserRepository users,
        IBinollaLinkRepository links,
        ISecretProtector protector,
        IJwtTokenService jwt,
        BinollaAppService binolla,
        IConfiguration configuration,
        ReferralAppService referrals,
        IBrokerSessionManager brokers,
        IBotAccessService access,
        IBinollaSessionRestorer restorer,
        IUserPasswordHasher passwords,
        ILogger<BinollaAuthAppService> logger)
    {
        _users = users;
        _links = links;
        _protector = protector;
        _jwt = jwt;
        _binolla = binolla;
        _configuration = configuration;
        _referrals = referrals;
        _brokers = brokers;
        _access = access;
        _restorer = restorer;
        _passwords = passwords;
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
            await _referrals.AttachReferrerAsync(user.Id, request.ReferralCode, ct);
        }

        if (user.IsMarketingDemo)
        {
            throw new ApiException(
                ApiErrorCodes.UseDemoLogin,
                "This is a marketing demo account. Sign in at /demo-login.",
                403);
        }

        await EnsureUserEmailAsync(user, email, ct);

        // FAST-PATH: If user is already registered and the provided password matches our record,
        // sign them in instantly (< 100ms) without forcing a 60-second headless browser launch.
        if (!provisioned)
        {
            var passwordVerified = false;
            if (!string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                passwordVerified = _passwords.Verify(user.PasswordHash, request.Password);
            }
            else if (!string.IsNullOrWhiteSpace(user.EncryptedLoginPassword))
            {
                try
                {
                    var decrypted = _protector.Decrypt(user.EncryptedLoginPassword);
                    if (string.Equals(decrypted, request.Password, StringComparison.Ordinal))
                    {
                        passwordVerified = true;
                        // Backfill PasswordHash for future argon/bcrypt verification
                        user.PasswordHash = _passwords.Hash(request.Password);
                        user.UpdatedAt = DateTimeOffset.UtcNow;
                        await _users.UpdateAsync(user, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed decrypting stored login password for user {UserId}", user.Id);
                }
            }

            if (passwordVerified)
            {
                var link = await _links.GetByUserIdAsync(user.Id, ct);
                var broker = Brokers.Normalize(request.Broker ?? link?.Broker);
                var liveClient = _brokers.Get(user.Id, broker);
                var isLive = liveClient is not null &&
                             liveClient.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;

                decimal? balance = null;
                if (isLive)
                {
                    try
                    {
                        using var balCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        balance = (await liveClient!.GetBalanceAsync(balCts.Token)).CurrentBalance;
                    }
                    catch { /* non-fatal */ }
                }
                else if (link is not null && !string.IsNullOrWhiteSpace(link.EncryptedSsid) && link.Status == BinollaLinkStatus.Connected)
                {
                    try
                    {
                        using var quickCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var storedSsid = _protector.Decrypt(link.EncryptedSsid);
                        var storedCookie = !string.IsNullOrWhiteSpace(link.EncryptedCookieHeader)
                            ? _protector.Decrypt(link.EncryptedCookieHeader)
                            : null;
                        var client = await _brokers.GetOrCreateAsync(
                            user.Id,
                            broker,
                            new BrokerCredentials(
                                Ssid: storedSsid,
                                CookieHeader: storedCookie,
                                Email: request.Email.Trim(),
                                Password: request.Password,
                                AccountType: link.AccountType == DomainAccount.Demo ? EngineAccount.Demo : EngineAccount.Real),
                            quickCts.Token);

                        if (client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
                        {
                            isLive = true;
                            try
                            {
                                using var balCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                                balance = (await client.GetBalanceAsync(balCts.Token)).CurrentBalance;
                            }
                            catch { /* non-fatal */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation(ex, "Quick SSID restore during login did not succeed for user {UserId}; background restore will handle it.", user.Id);
                        _restorer.EnsureBackgroundRestore(user.Id);
                    }
                }
                else
                {
                    _restorer.EnsureBackgroundRestore(user.Id);
                }

                var access = await _access.CheckAsync(user.Id, ct);
                _logger.LogInformation(
                    "Fast-path web login user={UserId} email={Email} isLive={IsLive} access={Access}",
                    user.Id, email, isLive, access.Access);

                return new BinollaAuthResponse(
                    AccessToken: _jwt.CreateToken(user),
                    UserId: user.Id.ToString(),
                    Connected: isLive,
                    AccountType: (link?.AccountType ?? DomainAccount.Real).ToString(),
                    Access: AccountAppService.MapAccess(access.Access),
                    AdminApproved: access.AdminApproved,
                    ApprovalStatus: access.ApprovalStatus,
                    LastConnectedAt: link?.LastConnectedAt,
                    Balance: balance);
            }
        }

        BinollaConnectResponse connect;
        try
        {
            connect = await _binolla.LoginWithCredentialsForUserAsync(user.Id, request, ct);
        }
        catch (ApiException ex) when (ex.Code == ApiErrorCodes.BinollaCaptchaRequired)
        {
            // The credentials were never judged — the broker interrupted with a human
            // check. The app account is real and already resolved, so signing the user in
            // here is what lets them answer the challenge: the guided-login endpoints
            // require a token, and failing the whole login would leave them without one.
            await StoreLoginPasswordAsync(user, request.Password, ct);
            _logger.LogInformation(
                "Binolla web login needs a human check user={UserId}", user.Id);

            return new BinollaAuthResponse(
                AccessToken: _jwt.CreateToken(user),
                UserId: user.Id.ToString(),
                Connected: false,
                AccountType: request.AccountType ?? "Real",
                Access: BotAccessState.BinollaNotConnected.ToString(),
                AdminApproved: false,
                ApprovalStatus: AdminApprovalStatus.Pending.ToString(),
                LastConnectedAt: null,
                Balance: null,
                RequiresGuidedLogin: true);
        }

        await StoreLoginPasswordAsync(user, request.Password, ct);

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
        await _referrals.AttachReferrerAsync(user.Id, request.ReferralCode, ct);
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

    /// <summary>
    /// Keeps the login password so a later silent re-login does not need the user again.
    /// Encrypted at rest; a failure here must never fail the sign-in itself.
    /// </summary>
    private async Task StoreLoginPasswordAsync(User user, string password, CancellationToken ct)
    {
        try
        {
            user.EncryptedLoginPassword = _protector.Encrypt(password);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(user, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to store encrypted login password for Binolla user {UserId}", user.Id);
        }
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
