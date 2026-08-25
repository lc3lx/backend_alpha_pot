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
    private readonly IBinollaSessionRestorer _restorer;
    private readonly IMarketingDemoService _demo;
    private readonly ILogger<BinollaAppService> _logger;

    public BinollaAppService(
        ICurrentUser currentUser,
        IBinollaLinkRepository links,
        ISecretProtector protector,
        IBinollaSessionManager sessions,
        IBotAccessService access,
        IBinollaCredentialAuth credentialAuth,
        IBinollaSessionRestorer restorer,
        IMarketingDemoService demo,
        ILogger<BinollaAppService> logger)
    {
        _currentUser = currentUser;
        _links = links;
        _protector = protector;
        _sessions = sessions;
        _access = access;
        _credentialAuth = credentialAuth;
        _restorer = restorer;
        _demo = demo;
        _logger = logger;
    }

    public async Task<BinollaConnectResponse> LoginWithCredentialsAsync(
        BinollaCredentialRequest request,
        CancellationToken ct) =>
        await LoginWithCredentialsForUserAsync(_currentUser.UserId, request, ct);

    public async Task<BinollaConnectResponse> SignUpWithCredentialsAsync(
        BinollaCredentialRequest request,
        CancellationToken ct) =>
        await SignUpWithCredentialsForUserAsync(_currentUser.UserId, request, ct);

    public async Task<BinollaConnectResponse> LoginWithCredentialsForUserAsync(
        Guid userId,
        BinollaCredentialRequest request,
        CancellationToken ct)
    {
        await EnsureNotMarketingDemoAsync(userId, ct);
        if (request is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Request body is required.");
        ValidateCredentialRequest(request);

        using var workCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workCt = workCts.Token;

        BinollaCapturedSession captured;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            captured = await _credentialAuth.LoginAsync(request.Email, request.Password, workCt);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binolla credential login failed for user {UserId}", userId);
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Unable to log into Binolla with the provided credentials.",
                400);
        }

        return await CompleteCredentialConnectForUserAsync(userId, captured, request, sw, workCt);
    }

    public async Task<BinollaConnectResponse> SignUpWithCredentialsForUserAsync(
        Guid userId,
        BinollaCredentialRequest request,
        CancellationToken ct)
    {
        await EnsureNotMarketingDemoAsync(userId, ct);
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
            _logger.LogWarning(ex, "Binolla credential signup failed for user {UserId}", userId);
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Unable to register on Binolla with the provided credentials.",
                400);
        }

        var connected = await ConnectAsync(
            userId,
            new BinollaConnectRequest(captured.SsidFrame, request.AccountType),
            ct,
            captured.CookieHeader);
        await PersistBinollaCredentialsAsync(userId, request.Email, request.Password, ct);
        return connected;
    }

    private async Task<BinollaConnectResponse> CompleteCredentialConnectForUserAsync(
        Guid userId,
        BinollaCapturedSession captured,
        BinollaCredentialRequest request,
        System.Diagnostics.Stopwatch sw,
        CancellationToken workCt)
    {
        var captureMs = sw.ElapsedMilliseconds;
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H100", "BinollaAppService.LoginWithCredentialsAsync", "capture_ok", new
        {
            captureMs,
            hasCookies = !string.IsNullOrWhiteSpace(captured.CookieHeader),
            cookieLen = captured.CookieHeader?.Length ?? 0,
            ssidLen = captured.SsidFrame?.Length ?? 0
        });

        const int maxConnectAttempts = 3;
        Exception? lastConnectError = null;
        for (var attempt = 1; attempt <= maxConnectAttempts; attempt++)
        {
            try
            {
                var result = await ConnectAsync(
                    userId,
                    new BinollaConnectRequest(captured.SsidFrame, request.AccountType),
                    workCt,
                    captured.CookieHeader);
                await PersistBinollaCredentialsAsync(userId, request.Email, request.Password, workCt);
                ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H100", "BinollaAppService.LoginWithCredentialsAsync", "login_ok", new
                {
                    captureMs,
                    totalMs = sw.ElapsedMilliseconds,
                    attempt
                });
                return result;
            }
            catch (Exception ex) when (
                attempt < maxConnectAttempts &&
                (ex is ApiException api &&
                 (api.Code is ApiErrorCodes.BinollaConnectionFailed
                     or ApiErrorCodes.BinollaSessionExpired
                     or ApiErrorCodes.BinollaNotConnected)))
            {
                lastConnectError = ex;
                ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H101", "BinollaAppService.LoginWithCredentialsAsync", "connect_retry", new
                {
                    captureMs,
                    attempt,
                    type = ex.GetType().Name,
                    code = (ex as ApiException)?.Code,
                    message = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message
                });
                try { await _sessions.RemoveAsync(userId.ToString(), workCt); } catch { /* ignore */ }
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), workCt);
            }
            catch (Exception ex)
            {
                ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H101", "BinollaAppService.LoginWithCredentialsAsync", "connect_after_capture_failed", new
                {
                    captureMs,
                    totalMs = sw.ElapsedMilliseconds,
                    attempt,
                    type = ex.GetType().Name,
                    code = (ex as ApiException)?.Code,
                    message = ex.Message.Length > 140 ? ex.Message[..140] : ex.Message
                });
                throw;
            }
        }

        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H101", "BinollaAppService.LoginWithCredentialsAsync", "connect_after_capture_failed", new
        {
            captureMs,
            totalMs = sw.ElapsedMilliseconds,
            attempt = maxConnectAttempts,
            type = lastConnectError?.GetType().Name,
            message = lastConnectError?.Message is { Length: > 140 } m ? m[..140] : lastConnectError?.Message
        });
        if (lastConnectError is not null) throw lastConnectError;
        throw new ApiException(ApiErrorCodes.BinollaConnectionFailed, "Unable to connect to Binolla.", 502);
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

    public Task<BinollaConnectResponse> ConnectAsync(
        BinollaConnectRequest request,
        CancellationToken ct,
        string? cookieHeader = null) =>
        ConnectAsync(_currentUser.UserId, request, ct, cookieHeader);

    public async Task<BinollaConnectResponse> ConnectAsync(
        Guid userId,
        BinollaConnectRequest request,
        CancellationToken ct,
        string? cookieHeader = null)
    {
        await EnsureNotMarketingDemoAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(request.Ssid))
            throw new ApiException(ApiErrorCodes.ValidationError, "ssid is required.");

        var accountType = ParseAccountType(request.AccountType);
        if (accountType == DomainAccount.Real)
            throw new ApiException(ApiErrorCodes.RealTradingDisabled, "Real trading is disabled in this phase.", 403);

        var encrypted = _protector.Encrypt(request.Ssid.Trim());

        try
        {
            using var workCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var workCt = workCts.Token;

            // Allow reconnect after a prior SSID auth failure / unauthorized drop.
            _restorer.ClearAuthFailure(userId);

            // Drop any zombie in-memory session so cookie+SSID attach on a fresh socket.
            await _sessions.RemoveAsync(userId.ToString(), workCt);

            var client = await _sessions.GetOrCreateAsync(
                userId.ToString(),
                request.Ssid.Trim(),
                workCt,
                cookieHeader);

            if (!client.IsTransportConnected ||
                client.Lifecycle is SessionLifecycleState.AuthenticationFailed
                    or SessionLifecycleState.SessionExpired
                    or SessionLifecycleState.Faulted
                    or SessionLifecycleState.Disconnected)
            {
                throw new BinollaAuthenticationException(
                    $"Binolla session not ready after connect (state={client.Lifecycle}).");
            }

            // Do not send a second account/change here — post-auth bootstrap already sets Demo.
            // A duplicate wire call raced unauthorized and dropped the fresh session within seconds.

            // Login must return as soon as the socket is authorized. Balance loads on the next market call.
            decimal? balanceValue = null;
            try
            {
                using var balCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                balanceValue = (await client.GetBalanceAsync(balCts.Token)).CurrentBalance;
            }
            catch
            {
                // balance is optional on connect
            }

            var now = DateTimeOffset.UtcNow;
            // Persist with workCt — never HttpContext.RequestAborted (PM2: OCE on Npgsql after
            // token capture while WS was already up → false BINOLLA_CONNECTION_FAILED).
            var link = await _links.GetByUserIdAsync(userId, workCt) ?? new BinollaLink
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
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                link.EncryptedCookieHeader = _protector.Encrypt(cookieHeader.Trim());
            link.AccountType = DomainAccount.Demo;
            link.Status = BinollaLinkStatus.Connected;
            link.LastConnectedAt = now;
            link.UpdatedAt = now;

            if (!wasApproved && !wasRejected)
            {
                link.AdminApproved = false;
                link.ApprovalStatus = AdminApprovalStatus.Pending;
            }

            await _links.UpsertAsync(link, workCt);

            var access = await _access.CheckAsync(userId, workCt);
            _logger.LogInformation(
                "Binolla linked user={UserId} approval={ApprovalStatus} access={Access}",
                userId, link.ApprovalStatus, access.Access);

            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H120", "BinollaAppService.ConnectAsync", "link_persisted", new
            {
                access = access.Access.ToString(),
                transportUp = client.IsTransportConnected,
                hasBalance = balanceValue is not null
            });
            // #endregion

            return new BinollaConnectResponse(
                Connected: true,
                AccountType: "Demo",
                Access: AccountAppService.MapAccess(access.Access),
                AdminApproved: access.AdminApproved,
                ApprovalStatus: access.ApprovalStatus,
                LastConnectedAt: link.LastConnectedAt,
                Balance: balanceValue);
        }
        catch (OperationCanceledException) when (
            _sessions.Get(userId.ToString()) is { } live &&
            live.IsTransportConnected &&
            live.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            // Client aborted HTTP after WS auth — session is live; salvage the link row.
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H120", "BinollaAppService.ConnectAsync", "http_abort_salvage", new
            {
                lifecycle = live.Lifecycle.ToString()
            });
            // #endregion
            try
            {
                var now = DateTimeOffset.UtcNow;
                var link = await _links.GetByUserIdAsync(userId, CancellationToken.None) ?? new BinollaLink
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    CreatedAt = now,
                    AdminApproved = false,
                    ApprovalStatus = AdminApprovalStatus.Pending
                };
                link.EncryptedSsid = encrypted;
                if (!string.IsNullOrWhiteSpace(cookieHeader))
                    link.EncryptedCookieHeader = _protector.Encrypt(cookieHeader.Trim());
                link.AccountType = DomainAccount.Demo;
                link.Status = BinollaLinkStatus.Connected;
                link.LastConnectedAt = now;
                link.UpdatedAt = now;
                await _links.UpsertAsync(link, CancellationToken.None);
                var access = await _access.CheckAsync(userId, CancellationToken.None);
                return new BinollaConnectResponse(
                    Connected: true,
                    AccountType: "Demo",
                    Access: AccountAppService.MapAccess(access.Access),
                    AdminApproved: access.AdminApproved,
                    ApprovalStatus: access.ApprovalStatus,
                    LastConnectedAt: link.LastConnectedAt,
                    Balance: null);
            }
            catch (Exception salvageEx)
            {
                _logger.LogWarning(salvageEx, "Binolla connect salvage failed for user {UserId}", userId);
                throw new ApiException(
                    ApiErrorCodes.BinollaConnectionFailed,
                    "Binolla connected but login response was interrupted. Re-open the app.",
                    502);
            }
        }
        catch (ApiException)
        {
            throw;
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session token is invalid or expired.", 401);
        }
        catch (BinollaConnectionException ex) when (
            ex.Message.Contains("AuthenticationFailed", StringComparison.Ordinal) ||
            ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaSessionExpired,
                "Binolla WebSocket rejected the session after login. Retry login.",
                401);
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
        if (await _demo.IsMarketingDemoAsync(userId, ct))
            return _demo.BuildStatus(userId);

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
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.BuildBalance(_currentUser.UserId);

        var access = await _access.CheckAsync(_currentUser.UserId, ct);
        AccountAppService.EnsureConnectedForMarket(access);

        IBinollaClient? client = null;
        for (var i = 0; i < 15; i++)
        {
            client = _sessions.Get(_currentUser.UserId.ToString());
            if (client is not null &&
                client.IsTransportConnected &&
                client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
            {
                break;
            }

            client = null;
            try { await Task.Delay(200, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }

        if (client is null)
        {
            // Background restore still warming — never 500 the shell.
            return new BinollaBalanceDto(
                Connected: false,
                AccountType: "Demo",
                DemoBalance: 0m,
                RealBalance: 0m,
                CurrentBalance: 0m);
        }

        try
        {
            using var balCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var balance = await client.GetBalanceAsync(balCts.Token);
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
        catch (Exception ex) when (ex is OperationCanceledException or BinollaTimeoutException)
        {
            // Do not block Home/Trading for a missing balance push — return a connected empty Demo snapshot.
            _logger.LogInformation(
                "Binolla balance not ready for user {UserId}; returning placeholder ({Error})",
                _currentUser.UserId, ex.GetType().Name);
            return new BinollaBalanceDto(
                Connected: true,
                AccountType: "Demo",
                DemoBalance: 0m,
                RealBalance: 0m,
                CurrentBalance: 0m);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Balance fetch failed for user {UserId}", _currentUser.UserId);
            return new BinollaBalanceDto(
                Connected: true,
                AccountType: "Demo",
                DemoBalance: 0m,
                RealBalance: 0m,
                CurrentBalance: 0m);
        }
    }

    public async Task<BinollaStatusDto> ChangeAccountTypeAsync(BinollaAccountTypeRequest request, CancellationToken ct)
    {
        await EnsureNotMarketingDemoAsync(ct);
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
        await EnsureNotMarketingDemoAsync(ct);
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

    /// <summary>
    /// Silent re-login using encrypted Binolla email/password saved on the link.
    /// Used when SSID/session expires so the user is not forced to type credentials again.
    /// </summary>
    public async Task<BinollaConnectResponse?> TryReloginFromStoredCredentialsAsync(CancellationToken ct)
    {
        await EnsureNotMarketingDemoAsync(ct);
        var userId = _currentUser.UserId;
        var link = await _links.GetByUserIdAsync(userId, ct);
        if (link is null ||
            string.IsNullOrWhiteSpace(link.EncryptedBinollaEmail) ||
            string.IsNullOrWhiteSpace(link.EncryptedBinollaPassword))
        {
            return null;
        }

        string email;
        string password;
        try
        {
            email = _protector.Decrypt(link.EncryptedBinollaEmail);
            password = _protector.Decrypt(link.EncryptedBinollaPassword);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return null;

        _restorer.ClearAuthFailure(userId);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "BR1",
            "BinollaAppService.TryReloginFromStoredCredentialsAsync",
            "relogin_start",
            new { hasEmail = email.Length > 0 });
        // #endregion

        return await LoginWithCredentialsAsync(
            new BinollaCredentialRequest(email, password, link.AccountType.ToString()),
            ct);
    }

    private async Task PersistBinollaCredentialsAsync(Guid userId, string email, string password, CancellationToken ct)
    {
        var link = await _links.GetByUserIdAsync(userId, ct);
        if (link is null) return;
        try
        {
            link.EncryptedBinollaEmail = _protector.Encrypt(email.Trim());
            link.EncryptedBinollaPassword = _protector.Encrypt(password);
            link.UpdatedAt = DateTimeOffset.UtcNow;
            await _links.UpsertAsync(link, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist Binolla credentials for user {UserId}", userId);
        }
    }

    private Task EnsureNotMarketingDemoAsync(CancellationToken ct) =>
        EnsureNotMarketingDemoAsync(_currentUser.UserId, ct);

    private async Task EnsureNotMarketingDemoAsync(Guid userId, CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(userId, ct))
        {
            throw new ApiException(
                ApiErrorCodes.Forbidden,
                "Marketing demo accounts use simulated data and cannot connect to Binolla.",
                403);
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
