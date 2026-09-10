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
    private readonly IBrokerSessionManager _brokers;
    private readonly IBrokerResolver _brokerResolver;
    private readonly IBinollaSessionRestorer _restorer;
    private readonly IMarketingDemoService _demo;
    private readonly IUserRepository _users;
    private readonly ILogger<BinollaAppService> _logger;

    public BinollaAppService(
        ICurrentUser currentUser,
        IBinollaLinkRepository links,
        ISecretProtector protector,
        IBinollaSessionManager sessions,
        IBotAccessService access,
        IBinollaCredentialAuth credentialAuth,
        IBrokerSessionManager brokers,
        IBrokerResolver brokerResolver,
        IBinollaSessionRestorer restorer,
        IMarketingDemoService demo,
        IUserRepository users,
        ILogger<BinollaAppService> logger)
    {
        _currentUser = currentUser;
        _links = links;
        _protector = protector;
        _sessions = sessions;
        _access = access;
        _credentialAuth = credentialAuth;
        _brokers = brokers;
        _brokerResolver = brokerResolver;
        _restorer = restorer;
        _demo = demo;
        _users = users;
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

        // Venues other than Binolla are served by the broker gateway, which owns their
        // protocol. Binolla keeps the credential-capture path below because it is live and
        // carries fixes that would be reopened by moving it.
        var broker = await ResolveLoginBrokerAsync(userId, request.Broker, ct).ConfigureAwait(false);
        if (broker != Brokers.Binolla)
            return await ConnectViaGatewayAsync(userId, broker, request, ct);

        // The throttle lives HERE, at the choke point every login funnels through, rather
        // than only on the background relogin. A client retrying POST /api/binolla/login
        // was spawning a fresh headless browser per request — five overlapping 40-second
        // captures were observed in production — and each one hitting a broker that is
        // already refusing us is what turns a temporary block into a durable IP ban.
        if (!_restorer.CanAttemptCredentialLogin(userId))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "A Binolla login attempt is already running or was just refused. "
                + "Wait a moment before trying again.",
                429);
        }

        using var workCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workCt = workCts.Token;

        BinollaCapturedSession captured;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            captured = await _credentialAuth.LoginAsync(request.Email, request.Password, workCt);
        }
        catch (ApiException ex) when (IsBrokerIpBlock(ex))
        {
            // The broker rejected the server itself. Pause everyone, not just this user.
            _restorer.MarkCredentialLoginFailed(userId, blockedByBroker: true);
            throw;
        }
        catch (ApiException)
        {
            _restorer.MarkCredentialLoginFailed(userId);
            throw;
        }
        catch (Exception ex)
        {
            _restorer.MarkCredentialLoginFailed(userId);
            _logger.LogWarning(ex, "Binolla credential login failed for user {UserId}", userId);
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Unable to log into Binolla with the provided credentials.",
                400);
        }

        // Captured a session: release the throttle so the next outage retries promptly.
        _restorer.ClearAuthFailure(userId);
        return await CompleteCredentialConnectForUserAsync(userId, captured, request, sw, workCt);
    }

    /// <summary>
    /// Which venue a login is for.
    ///
    /// <para>An omitted broker used to mean Binolla, full stop. That default is correct
    /// only for an account that has never chosen — and it silently sent Quotex users into
    /// Binolla twice: once from the background reconnect, once from the re-link screen,
    /// both of which built a request without the field. Neither failed loudly, because
    /// guessing Binolla is always a valid-looking answer.</para>
    ///
    /// <para>So the user's own link decides whenever the caller did not say. The plain
    /// default now applies only to someone who has no link at all.</para>
    /// </summary>
    private async Task<string> ResolveLoginBrokerAsync(
        Guid userId, string? requested, CancellationToken ct)
    {
        if (Brokers.IsKnown(requested))
            return Brokers.Normalize(requested);

        var link = await _links.GetByUserIdAsync(userId, ct).ConfigureAwait(false);
        var linked = Brokers.Normalize(link?.Broker);

        if (link is not null && linked != Brokers.Default)
        {
            _logger.LogInformation(
                "Login for user {UserId} did not name a broker; using their linked {Broker}",
                userId, linked);
        }

        return linked;
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

    /// <summary>
    /// Finishes a credential login from a captured session, whoever captured it.
    ///
    /// <para>Guided login (the user solving the CAPTCHA themselves) ends up holding the
    /// same token the automated capture produces, and must be recorded identically —
    /// approval state, encryption at rest, account type. Sharing this step is what keeps
    /// the two paths from drifting into different rules.</para>
    /// </summary>
    public Task<BinollaConnectResponse> CompleteCredentialConnectAsync(
        Guid userId,
        BinollaCapturedSession captured,
        BinollaCredentialRequest request,
        CancellationToken ct) =>
        CompleteCredentialConnectForUserAsync(
            userId, captured, request, System.Diagnostics.Stopwatch.StartNew(), ct);

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

        // Live is the default and demo is the privilege. Anyone may connect live; asking
        // for demo requires an admin to have unlocked it for this account.
        var accountType = ParseAccountType(request.AccountType);
        await EnsureAccountTypeAllowedAsync(userId, accountType, ct);

        var encrypted = _protector.Encrypt(request.Ssid.Trim());

        try
        {
            using var workCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var workCt = workCts.Token;

            // Allow reconnect after a prior SSID auth failure / unauthorized drop.
            _restorer.ClearAuthFailure(userId);

            // Drop any zombie in-memory session so cookie+SSID attach on a fresh socket.
            await _sessions.RemoveAsync(userId.ToString(), workCt);

            // The balance is chosen HERE, in the session's post-auth bootstrap. Sending a
            // second account/change after connect races the unauthorized window and drops
            // the fresh socket, which is why this has to be passed in rather than switched
            // afterwards.
            var client = await _sessions.GetOrCreateAsync(
                userId.ToString(),
                request.Ssid.Trim(),
                workCt,
                cookieHeader,
                accountType == DomainAccount.Demo ? EngineAccount.Demo : EngineAccount.Real);

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
            // Honour what was actually requested (and validated above). This used to be
            // pinned to Demo, which is why live was unreachable whatever the caller sent.
            link.AccountType = accountType;
            // Stamped on every connect, not only on the gateway path: a user coming BACK
            // from Quotex would otherwise keep "quotex" on their link, and every worker
            // would then look for their session on a venue they are no longer using.
            link.Broker = Brokers.Binolla;
            link.Status = BinollaLinkStatus.Connected;
            link.LastConnectedAt = now;
            link.UpdatedAt = now;

            if (!wasApproved && !wasRejected)
            {
                link.AdminApproved = false;
                link.ApprovalStatus = AdminApprovalStatus.Pending;
            }

            await _links.UpsertAsync(link, workCt);
            _brokerResolver.Invalidate(userId);

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
                AccountType: accountType.ToString(),
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
                link.AccountType = accountType;
                link.Broker = Brokers.Binolla;
                link.Status = BinollaLinkStatus.Connected;
                link.LastConnectedAt = now;
                link.UpdatedAt = now;
                await _links.UpsertAsync(link, CancellationToken.None);
                _brokerResolver.Invalidate(userId);
                var access = await _access.CheckAsync(userId, CancellationToken.None);
                return new BinollaConnectResponse(
                    Connected: true,
                    AccountType: accountType.ToString(),
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
        // The link records the venue, so it answers the routing question directly. Asking
        // Binolla's manager unconditionally — as this did — reported every Quotex user as
        // disconnected no matter how healthy their session was.
        var broker = Brokers.Normalize(link?.Broker);
        var client = _brokers.Get(userId, broker);

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
            WebSocketConnected: connected,
            Broker: broker);
    }

    public async Task<BinollaBalanceDto> GetBalanceAsync(CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.BuildBalance(_currentUser.UserId);

        var access = await _access.CheckAsync(_currentUser.UserId, ct);
        AccountAppService.EnsureConnectedForMarket(access);

        // The user's own venue. Asking Binolla's manager unconditionally — as this did —
        // never found a Quotex session, so every balance poll spun the full wait below
        // and then reported a zero balance on a perfectly healthy account.
        var link = await _links.GetByUserIdAsync(_currentUser.UserId, ct).ConfigureAwait(false);
        var broker = Brokers.Normalize(link?.Broker);

        IBrokerClient? client = null;
        // Short, and deliberately so. The wait exists for the seconds right after a login
        // while the socket finishes its handshake; this endpoint is also POLLED by every
        // open page, and the old 15 x 200ms spin cost three seconds of a request thread
        // on every one of those polls whenever a session was not up.
        for (var i = 0; i < 4; i++)
        {
            client = _brokers.Get(_currentUser.UserId, broker);
            if (client is not null &&
                client.IsTransportConnected &&
                client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
            {
                break;
            }

            client = null;
            try { await Task.Delay(150, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }

        if (client is null)
        {
            // Nothing live: get a restore moving so the NEXT poll can succeed, instead of
            // making this one wait for it.
            _restorer.EnsureBackgroundRestore(_currentUser.UserId);
        }

        if (client is null)
        {
            // Background restore still warming — never 500 the shell.
            return new BinollaBalanceDto(
                Connected: false,
                // Placeholder while the session warms. Live is the product default, so a
                // hardcoded "Demo" here would tell a live user they are on demo money.
                AccountType: nameof(DomainAccount.Real),
                DemoBalance: 0m,
                RealBalance: 0m,
                CurrentBalance: 0m);
        }

        try
        {
            using var balCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var balance = await client.GetBalanceAsync(balCts.Token);
            var accountType = balance.CurrentType == EngineAccount.Real ? "Real" : "Demo";
            var current =
                balance.CurrentType == EngineAccount.Real ? balance.RealBalance : balance.DemoBalance;
            return new BinollaBalanceDto(
                Connected: true,
                AccountType: accountType,
                DemoBalance: balance.DemoBalance,
                RealBalance: balance.RealBalance,
                CurrentBalance: current);
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
        await EnsureAccountTypeAllowedAsync(_currentUser.UserId, accountType, ct);

        var client = await RequireConnectedClientAsync(ct).ConfigureAwait(false);
        var engineType = accountType == DomainAccount.Real ? EngineAccount.Real : EngineAccount.Demo;
        await client.ChangeAccountAsync(engineType, ct);

        var link = await _links.GetByUserIdAsync(_currentUser.UserId, ct);
        if (link is not null)
        {
            link.AccountType = accountType;
            link.UpdatedAt = DateTimeOffset.UtcNow;
            await _links.UpsertAsync(link, ct);
        }

        return await GetStatusAsync(ct);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await EnsureNotMarketingDemoAsync(ct);
        var userId = _currentUser.UserId;

        var link = await _links.GetByUserIdAsync(userId, ct);
        // Closing Binolla's session for a Quotex user left the Quotex socket open and
        // still trading while the UI showed the account as disconnected.
        await _brokers.RemoveAsync(userId, Brokers.Normalize(link?.Broker), ct);

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
    /// <summary>
    /// Whether the failure was the broker refusing this SERVER rather than the account —
    /// an HTTP 403 / geo block. Those are IP-wide, so they must pause every user.
    /// </summary>
    /// <summary>
    /// Opens a session on a gateway-backed broker and records the link, mirroring what the
    /// Binolla path does once its capture succeeds.
    /// </summary>
    private async Task<BinollaConnectResponse> ConnectViaGatewayAsync(
        Guid userId,
        string broker,
        BinollaCredentialRequest request,
        CancellationToken ct)
    {
        var accountType = ParseAccountType(request.AccountType);
        await EnsureAccountTypeAllowedAsync(userId, accountType, ct);

        var engineType = accountType == DomainAccount.Demo ? EngineAccount.Demo : EngineAccount.Real;
        var client = await _brokers.GetOrCreateAsync(
            userId,
            broker,
            new BrokerCredentials(
                Email: request.Email.Trim(),
                Password: request.Password,
                AccountType: engineType),
            ct);

        var now = DateTimeOffset.UtcNow;
        var link = await _links.GetByUserIdAsync(userId, ct) ?? new BinollaLink
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            AdminApproved = false,
            ApprovalStatus = AdminApprovalStatus.Pending
        };

        link.Broker = broker;
        link.AccountType = accountType;
        link.Status = BinollaLinkStatus.Connected;
        link.LastConnectedAt = now;
        link.UpdatedAt = now;
        link.EncryptedBinollaEmail = _protector.Encrypt(request.Email.Trim());
        link.EncryptedBinollaPassword = _protector.Encrypt(request.Password);
        await _links.UpsertAsync(link, ct);
        // The routing cache must see the new venue immediately — a bot reading a stale
        // value would place this user's next trade through the wrong broker's client.
        _brokerResolver.Invalidate(userId);

        decimal? balance = null;
        try
        {
            balance = (await client.GetBalanceAsync(ct)).CurrentBalance;
        }
        catch
        {
            // Balance is optional on connect; the next market call fetches it.
        }

        _logger.LogInformation(
            "Broker {Broker} connected for user {UserId}", broker, userId);

        // Access/approval come from the same gate as the Binolla path, so a Quotex user is
        // held to identical admin approval rules.
        var access = await _access.CheckAsync(userId, ct);

        return new BinollaConnectResponse(
            Connected: true,
            AccountType: accountType.ToString(),
            Access: AccountAppService.MapAccess(access.Access),
            AdminApproved: access.AdminApproved,
            ApprovalStatus: access.ApprovalStatus,
            LastConnectedAt: link.LastConnectedAt,
            Balance: balance);
    }

    private static bool IsBrokerIpBlock(ApiException ex) =>
        ex.Message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("blocked this server IP", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("geo-restriction", StringComparison.OrdinalIgnoreCase);

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

        // No throttle check here: LoginWithCredentialsForUserAsync owns it, so the guard
        // lives at the one place every login path funnels through. Claiming the slot twice
        // would make this method block its own inner call and never attempt anything.
        //
        // This used to call ClearAuthFailure() before trying, which wiped the very cooldown
        // meant to stop this loop — the bot worker calls this every tick while a session is
        // down, so a refusing broker got a fresh 40-second browser every few seconds.

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "BR1",
            "BinollaAppService.TryReloginFromStoredCredentialsAsync",
            "relogin_start",
            new { hasEmail = email.Length > 0 });
        // #endregion

        try
        {
            // Throttling and failure marking happen inside; a refusal (including the
            // "already running / just refused" 429) simply means no session this round.
            // The venue MUST come from the link. Left off, it normalised to Binolla and a
            // Quotex user's background reconnect ran Binolla's browser capture against
            // their Quotex credentials — which is how signing in to Quotex ended up
            // logging people into Binolla instead.
            return await LoginWithCredentialsAsync(
                new BinollaCredentialRequest(
                    email,
                    password,
                    link.AccountType.ToString(),
                    ReferralCode: null,
                    Broker: Brokers.Normalize(link.Broker)),
                ct);
        }
        catch (ApiException)
        {
            // Background restore is best-effort — never surface this to the caller's loop.
            return null;
        }
    }

    private async Task PersistBinollaCredentialsAsync(Guid userId, string email, string password, CancellationToken ct)
    {
        var link = await _links.GetByUserIdAsync(userId, ct);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug281dcf.Write(
            "B",
            "BinollaAppService.PersistBinollaCredentialsAsync",
            "persist_entry",
            new
            {
                userId = userId.ToString(),
                linkNull = link is null,
                emailLen = email?.Trim().Length ?? 0,
                passwordLen = password?.Length ?? 0
            });
        // #endregion
        if (link is null) return;
        try
        {
            link.EncryptedBinollaEmail = _protector.Encrypt(email.Trim());
            link.EncryptedBinollaPassword = _protector.Encrypt(password);
            link.UpdatedAt = DateTimeOffset.UtcNow;
            await _links.UpsertAsync(link, ct);
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug281dcf.Write(
                "B",
                "BinollaAppService.PersistBinollaCredentialsAsync",
                "persist_ok",
                new
                {
                    userId = userId.ToString(),
                    linkId = link.Id.ToString(),
                    hasEncryptedEmail = !string.IsNullOrWhiteSpace(link.EncryptedBinollaEmail),
                    hasEncryptedPassword = !string.IsNullOrWhiteSpace(link.EncryptedBinollaPassword)
                });
            // #endregion
        }
        catch (Exception ex)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug281dcf.Write(
                "B",
                "BinollaAppService.PersistBinollaCredentialsAsync",
                "persist_fail",
                new { userId = userId.ToString(), error = ex.GetType().Name });
            // #endregion
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

    /// <summary>
    /// The user's live client on whichever venue they trade, or a 409.
    ///
    /// <para>Broker-aware because switching a Quotex user's balance has to reach Quotex.
    /// Looking the session up on Binolla's manager reported "not connected" to an account
    /// whose session was perfectly healthy.</para>
    /// </summary>
    private async Task<IBrokerClient> RequireConnectedClientAsync(CancellationToken ct)
    {
        var link = await _links.GetByUserIdAsync(_currentUser.UserId, ct).ConfigureAwait(false);
        var broker = Brokers.Normalize(link?.Broker);

        var client = _brokers.Get(_currentUser.UserId, broker);
        if (client is null ||
            client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaNotConnected, $"Connect {broker} before continuing.", 409);
        }

        return client;
    }

    /// <summary>
    /// Blocks the Binolla DEMO balance unless an admin has unlocked it for this user.
    ///
    /// <para>Live trading is the default state of the product; demo is a concession an
    /// admin grants per account. Enforced here rather than at the endpoint so connect,
    /// re-login and the runtime account switch all go through the same rule.</para>
    /// </summary>
    private async Task EnsureAccountTypeAllowedAsync(
        Guid userId,
        DomainAccount accountType,
        CancellationToken ct)
    {
        if (accountType != DomainAccount.Demo)
            return;

        var user = await _users.GetByIdAsync(userId, ct);
        if (user?.DemoAllowed == true)
            return;

        throw new ApiException(
            ApiErrorCodes.DemoAccountLocked,
            "The demo account is locked. Ask an administrator to enable it for your account.",
            403);
    }

    private static DomainAccount ParseAccountType(string? value)
    {
        // Unspecified means live. Demo is now opt-in and admin-gated.
        if (string.IsNullOrWhiteSpace(value))
            return DomainAccount.Real;

        return value.Trim().ToLowerInvariant() switch
        {
            "demo" => DomainAccount.Demo,
            "real" or "live" => DomainAccount.Real,
            _ => throw new ApiException(ApiErrorCodes.ValidationError, "accountType must be Demo or Real.")
        };
    }
}
