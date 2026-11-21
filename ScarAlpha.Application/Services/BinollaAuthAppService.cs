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
                    Balance: balance,
                    // Quotex not live right now (never linked, or the session dropped):
                    // the client opens the guided login instead of an empty dashboard.
                    Queued: !isLive && link is not null &&
                            string.Equals(broker, "quotex", StringComparison.OrdinalIgnoreCase));
            }
        }

        BinollaConnectResponse connect;
        try
        {
            connect = await _binolla.LoginWithCredentialsForUserAsync(user.Id, request, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !NeedsTheUser(ex))
        {
            // The broker could not be reached or would not let this server in: a human
            // check, Cloudflare, a dead proxy tunnel, a timeout. None of it is the user's
            // fault, so the user is not shown an error. A Quotex user goes on the waiting
            // list with the credentials saved on the link, and the reconnect endpoint links
            // the account once the broker lets it in. Binolla keeps its guided login for a
            // CAPTCHA; any other Binolla failure is reported as before.
            var captcha = ex is ApiException { Code: ApiErrorCodes.BinollaCaptchaRequired };
            await StoreLoginPasswordAsync(user, request.Password, ct);
            var queued = await QueueForBrokerAsync(user, request, ct);
            _logger.LogInformation(
                "Broker login did not complete user={UserId} queued={Queued} reason={Reason}",
                user.Id, queued, ex.Message.Length > 160 ? ex.Message[..160] : ex.Message);
            if (!queued && !captcha)
                throw;

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
                RequiresGuidedLogin: !queued,
                Queued: queued);
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
    /// <summary>
    /// Saves a Quotex user's credentials on a pending, disconnected link — the waiting list.
    /// Returns false for any other broker (Binolla keeps its guided login) or on failure.
    /// An existing link keeps its approval and connection history; only the credentials and
    /// venue are refreshed, so a re-queued user is not reset to unapproved.
    /// </summary>
    /// <summary>
    /// Failures only the user can fix, which waiting would never resolve: a wrong password,
    /// or Quotex mailing a PIN that must be confirmed on Quotex first. These are still shown.
    /// </summary>
    private static bool NeedsTheUser(Exception ex) =>
        ex is ApiException api &&
        (api.Code is ApiErrorCodes.QuotexPinRequired or ApiErrorCodes.InvalidCredentials ||
         api.Message.Contains("Invalid email or password", StringComparison.OrdinalIgnoreCase) ||
         api.Message.Contains("wrong password", StringComparison.OrdinalIgnoreCase) ||
         api.Message.Contains("This account was deleted", StringComparison.OrdinalIgnoreCase));

    private async Task<bool> QueueForBrokerAsync(User user, BinollaCredentialRequest request, CancellationToken ct)
    {
        try
        {
            var link = await _links.GetByUserIdAsync(user.Id, ct);
            var broker = Brokers.Normalize(request.Broker ?? link?.Broker);
            if (!string.Equals(broker, "quotex", StringComparison.OrdinalIgnoreCase))
                return false;

            var now = DateTimeOffset.UtcNow;
            link ??= new BinollaLink
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AccountType = DomainAccount.Real,
                Status = BinollaLinkStatus.Disconnected,
                AdminApproved = false,
                ApprovalStatus = AdminApprovalStatus.Pending,
                CreatedAt = now,
            };
            link.Broker = broker;
            link.EncryptedBinollaEmail = _protector.Encrypt(request.Email.Trim());
            link.EncryptedBinollaPassword = _protector.Encrypt(request.Password);
            link.UpdatedAt = now;
            await _links.UpsertAsync(link, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue user {UserId} for the broker", user.Id);
            return false;
        }
    }

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
