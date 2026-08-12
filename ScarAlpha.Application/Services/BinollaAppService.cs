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

public sealed class BinollaAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBinollaLinkRepository _links;
    private readonly ISecretProtector _protector;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBotAccessService _access;
    private readonly IBinollaCredentialAuth _credentialAuth;
    private readonly ILogger<BinollaAppService> _logger;

    public BinollaAppService(
        ICurrentUser currentUser,
        IBinollaLinkRepository links,
        ISecretProtector protector,
        IBinollaSessionManager sessions,
        IBotAccessService access,
        IBinollaCredentialAuth credentialAuth,
        ILogger<BinollaAppService> logger)
    {
        _currentUser = currentUser;
        _links = links;
        _protector = protector;
        _sessions = sessions;
        _access = access;
        _credentialAuth = credentialAuth;
        _logger = logger;
    }

    public async Task<BinollaConnectResponse> LoginWithCredentialsAsync(
        BinollaCredentialRequest request,
        CancellationToken ct)
    {
        if (request is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Request body is required.");
        ValidateCredentialRequest(request);

        BinollaCapturedSession captured;
        try
        {
            captured = await _credentialAuth.LoginAsync(request.Email, request.Password, ct);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binolla credential login failed for user {UserId}", _currentUser.UserId);
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Unable to log into Binolla with the provided credentials.",
                400);
        }

        // Password is never stored — only the resulting SSID is encrypted via ConnectAsync.
        return await ConnectAsync(
            new BinollaConnectRequest(captured.SsidFrame, request.AccountType),
            ct,
            captured.CookieHeader);
    }

    public async Task<BinollaConnectResponse> SignUpWithCredentialsAsync(
        BinollaCredentialRequest request,
        CancellationToken ct)
    {
        if (request is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Request body is required.");
        ValidateCredentialRequest(request);

        BinollaCapturedSession captured;
        try
        {
            captured = await _credentialAuth.SignUpAsync(request.Email, request.Password, ct);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binolla credential signup failed for user {UserId}", _currentUser.UserId);
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Unable to register on Binolla with the provided credentials.",
                400);
        }

        return await ConnectAsync(
            new BinollaConnectRequest(captured.SsidFrame, request.AccountType),
            ct,
            captured.CookieHeader);
    }

    private static void ValidateCredentialRequest(BinollaCredentialRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length < 3 || !request.Email.Contains('@'))
            throw new ApiException(ApiErrorCodes.ValidationError, "A valid Binolla email is required.");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 4)
            throw new ApiException(ApiErrorCodes.ValidationError, "Binolla password is required.");
        if (request.Password.Length > 128 || request.Email.Length > 256)
            throw new ApiException(ApiErrorCodes.ValidationError, "Credentials exceed allowed length.");
    }

    public async Task<BinollaConnectResponse> ConnectAsync(
        BinollaConnectRequest request,
        CancellationToken ct,
        string? cookieHeader = null)
    {
        if (string.IsNullOrWhiteSpace(request.Ssid))
            throw new ApiException(ApiErrorCodes.ValidationError, "ssid is required.");

        var accountType = ParseAccountType(request.AccountType);
        if (accountType == DomainAccount.Real)
            throw new ApiException(ApiErrorCodes.RealTradingDisabled, "Real trading is disabled in this phase.", 403);

        var userId = _currentUser.UserId;
        var encrypted = _protector.Encrypt(request.Ssid.Trim());

        try
        {
            var client = await _sessions.GetOrCreateAsync(
                userId.ToString(),
                request.Ssid.Trim(),
                ct,
                cookieHeader);
            await client.ChangeAccountAsync(EngineAccount.Demo, ct);

            // Do not block login on a full balance wait (was misreported as auth timeout for ~60s).
            decimal? balanceValue = null;
            try
            {
                using var balCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                balCts.CancelAfter(TimeSpan.FromSeconds(3));
                var balance = await client.GetBalanceAsync(balCts.Token);
                balanceValue = balance.DemoBalance;
            }
            catch (Exception ex) when (ex is OperationCanceledException or BinollaTimeoutException)
            {
                _logger.LogInformation(
                    "Binolla balance not ready yet after connect for user {UserId}; continuing without blocking login ({Error})",
                    userId, ex.GetType().Name);
            }

            var now = DateTimeOffset.UtcNow;
            var link = await _links.GetByUserIdAsync(userId, ct) ?? new BinollaLink
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                AdminApproved = false,
                ApprovalStatus = AdminApprovalStatus.Pending
            };

            // Preserve existing admin approval across reconnects; never auto-approve.
            var wasApproved = link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Approved;
            var wasRejected = link.ApprovalStatus == AdminApprovalStatus.Rejected;

            link.EncryptedSsid = encrypted;
            link.AccountType = DomainAccount.Demo;
            link.Status = BinollaLinkStatus.Connected;
            link.LastConnectedAt = now;
            link.UpdatedAt = now;

            if (!wasApproved && !wasRejected)
            {
                link.AdminApproved = false;
                link.ApprovalStatus = AdminApprovalStatus.Pending;
            }

            await _links.UpsertAsync(link, ct);

            var access = await _access.CheckAsync(userId, ct);
            _logger.LogInformation(
                "Binolla linked user={UserId} approval={ApprovalStatus} access={Access}",
                userId, link.ApprovalStatus, access.Access);

            return new BinollaConnectResponse(
                Connected: true,
                AccountType: "Demo",
                Access: AccountAppService.MapAccess(access.Access),
                AdminApproved: access.AdminApproved,
                ApprovalStatus: access.ApprovalStatus,
                LastConnectedAt: link.LastConnectedAt,
                Balance: balanceValue);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session token is invalid or expired.", 401);
        }
        catch (BinollaTimeoutException)
        {
            throw new ApiException(
                ApiErrorCodes.BinollaConnectionFailed,
                "Binolla WebSocket authentication timed out after login token capture. Retry login.",
                504);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binolla connect failed for user {UserId}", userId);
            throw new ApiException(ApiErrorCodes.BinollaConnectionFailed, "Unable to connect to Binolla.", 502);
        }
    }

    public async Task<BinollaStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var link = await _links.GetByUserIdAsync(userId, ct);
        var client = _sessions.Get(userId.ToString());

        var connected = client is not null &&
                        client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;

        decimal? balance = null;
        if (connected)
        {
            try { balance = (await client!.GetBalanceAsync(ct)).CurrentBalance; }
            catch { /* status still useful without balance */ }
        }

        return new BinollaStatusDto(
            Connected: connected,
            AccountType: (link?.AccountType ?? DomainAccount.Demo).ToString(),
            Status: connected
                ? nameof(BinollaLinkStatus.Connected)
                : (link?.Status.ToString() ?? nameof(BinollaLinkStatus.Disconnected)),
            LastConnectedAt: link?.LastConnectedAt,
            Balance: balance,
            Lifecycle: client?.Lifecycle.ToString() ?? "None",
            WebSocketConnected: connected);
    }

    public async Task<BinollaBalanceDto> GetBalanceAsync(CancellationToken ct)
    {
        var access = await _access.CheckAsync(_currentUser.UserId, ct);
        AccountAppService.EnsureConnectedForMarket(access);

        var client = RequireConnectedClient();
        try
        {
            var balance = await client.GetBalanceAsync(ct);
            // Real trading is disabled: never surface Real balance as actionable funds.
            return new BinollaBalanceDto(
                Connected: true,
                AccountType: "Demo",
                DemoBalance: balance.DemoBalance,
                RealBalance: 0m,
                CurrentBalance: balance.DemoBalance);
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Balance fetch failed for user {UserId}", _currentUser.UserId);
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Binolla session is not available.", 409);
        }
    }

    public async Task<BinollaStatusDto> ChangeAccountTypeAsync(BinollaAccountTypeRequest request, CancellationToken ct)
    {
        var accountType = ParseAccountType(request.AccountType);
        if (accountType == DomainAccount.Real)
            throw new ApiException(ApiErrorCodes.RealTradingDisabled, "Real trading is disabled in this phase.", 403);

        var client = RequireConnectedClient();
        await client.ChangeAccountAsync(EngineAccount.Demo, ct);

        var link = await _links.GetByUserIdAsync(_currentUser.UserId, ct);
        if (link is not null)
        {
            link.AccountType = DomainAccount.Demo;
            link.UpdatedAt = DateTimeOffset.UtcNow;
            await _links.UpsertAsync(link, ct);
        }

        return await GetStatusAsync(ct);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        await _sessions.DisconnectAsync(userId.ToString(), ct);

        var link = await _links.GetByUserIdAsync(userId, ct);
        if (link is not null)
        {
            link.Status = BinollaLinkStatus.Disconnected;
            link.UpdatedAt = DateTimeOffset.UtcNow;
            await _links.UpsertAsync(link, ct);
        }
    }

    private IBinollaClient RequireConnectedClient()
    {
        var client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is null ||
            client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
        {
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Connect Binolla before continuing.", 409);
        }

        return client;
    }

    private static DomainAccount ParseAccountType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DomainAccount.Demo;

        return value.Trim().ToLowerInvariant() switch
        {
            "demo" => DomainAccount.Demo,
            "real" => DomainAccount.Real,
            _ => throw new ApiException(ApiErrorCodes.ValidationError, "accountType must be Demo or Real.")
        };
    }
}
