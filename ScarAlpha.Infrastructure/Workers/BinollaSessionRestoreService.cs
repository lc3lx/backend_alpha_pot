using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Services;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Domain.Enums;
using EngineAccount = ScarAlpha.Binolla.Models.AccountType;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// On API start: load approved EncryptedSsid values, decrypt, reconnect asynchronously.
/// Partial failure never crashes the host. SSID material is never logged.
/// </summary>
public sealed class BinollaSessionRestoreService : IBinollaSessionRestorer, IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBrokerSessionManager _brokerSessions;
    private readonly ISecretProtector _protector;
    private readonly BinollaSessionRestoreOptions _options;
    private readonly ILogger<BinollaSessionRestoreService> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _authFailed = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userGates = new();
    private readonly ConcurrentDictionary<Guid, byte> _bgRestoreQueued = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _retryAfterUtc = new();
    private readonly TaskCompletionSource _initialDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;

    public BinollaSessionRestoreService(
        IServiceScopeFactory scopeFactory,
        IBinollaSessionManager sessions,
        IBrokerSessionManager brokerSessions,
        ISecretProtector protector,
        IOptions<BinollaSessionRestoreOptions> options,
        ILogger<BinollaSessionRestoreService> logger)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
        _brokerSessions = brokerSessions;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
    }

    public Task WhenInitialRestoreCompleted => _initialDone.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunStartupRestoreAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _initialDone.TrySetResult();
        return Task.CompletedTask;
    }

    private async Task RunStartupRestoreAsync(CancellationToken ct)
    {
        try
        {
            await RestoreApprovedSessionsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            // Must never crash the application.
            _logger.LogError(ex, "Binolla session restore wave failed unexpectedly; continuing without full restore");
        }
        finally
        {
            _initialDone.TrySetResult();
        }
    }

    public async Task RestoreApprovedSessionsAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Binolla session restore is disabled");
            return;
        }

        IReadOnlyList<(Guid UserId, Guid LinkId)> targets;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var links = scope.ServiceProvider.GetRequiredService<IBinollaLinkRepository>();
            // Restore approved AND pending connected links — pending users were only lazy-restored
            // on first market call (~23–25s cold), which blocked assets/candles.
            var all = await links.ListAsync(approvalStatus: null, ct).ConfigureAwait(false);
            targets = all
                .Where(l =>
                    l.Status == BinollaLinkStatus.Connected &&
                    HasRestorableSecret(l) &&
                    (l.ApprovalStatus == AdminApprovalStatus.Approved ||
                     l.ApprovalStatus == AdminApprovalStatus.Pending) &&
                    l.ApprovalStatus != AdminApprovalStatus.Rejected)
                .Select(l => (l.UserId, l.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Binolla links for session restore");
            return;
        }

        _logger.LogInformation(
            "Binolla session restore starting for {Count} approved/pending linked user(s); parallelism={Parallelism}",
            targets.Count, Math.Max(1, _options.MaxDegreeOfParallelism));

        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxDegreeOfParallelism));
        var tasks = targets.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RestoreOneAsync(item.UserId, maxAttempts: _options.MaxAttempts, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        _logger.LogInformation(
            "Binolla session restore wave finished; activeSessions={Active}",
            _sessions.ActiveSessionCount);
    }

    public async Task<bool> TryRestoreUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return IsLive(userId);

        if (IsLive(userId))
            return true;

        if (_authFailed.ContainsKey(userId))
            return false;

        if (IsCoolingDown(userId))
            return false;

        return await RestoreOneAsync(userId, maxAttempts: Math.Max(1, _options.LazyMaxAttempts), ct)
            .ConfigureAwait(false);
    }

    public void ClearAuthFailure(Guid userId)
    {
        _authFailed.TryRemove(userId, out _);
        _retryAfterUtc.TryRemove(userId, out _);
        _credentialFailures.TryRemove(userId, out _);
        _credentialInFlight.TryRemove(userId, out _);
        ReleaseGlobalSlot(userId);

        lock (_globalGate)
        {
            // A capture that succeeded proves the IP is fine again.
            _globalFailures = 0;
            _globalRetryAfterUtc = DateTimeOffset.MinValue;
            // …and that the spacing floor should stop holding anyone else back.
            _recentCaptureFailures = 0;
            _lastCaptureStartedUtc = DateTimeOffset.MinValue;

            // The spacing floor exists to throttle FAILED attempts against a broker that
            // is refusing us. After a success it would only delay other users onboarding
            // for no benefit — and they are still serialised by the single capture slot.
            _lastCaptureStartedUtc = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Consecutive failed credential logins, for the backoff below.</summary>
    private readonly ConcurrentDictionary<Guid, int> _credentialFailures = new();

    /// <summary>One in-flight credential login per user; a second would just queue browsers.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _credentialInFlight = new();

    /// <summary>
    /// Who currently holds the single global capture slot, if anyone.
    ///
    /// <para>A credential capture drives a headless browser and, more importantly, hits
    /// Binolla from the server's ONE public IP. Per-user backoff protects each account but
    /// does nothing about the shared resource: with five bots holding dead sessions, five
    /// independent 30-second cooldowns still produced a login attempt roughly every six
    /// seconds, which is what the broker sees and blocks.</para>
    /// </summary>
    /// <summary>
    /// Users whose credential capture is running right now.
    ///
    /// <para>Bounded rather than singular. A Binolla capture drives a headless browser,
    /// which is genuinely expensive, so it cannot be unlimited — but allowing exactly one
    /// meant a person clicking "sign in" was refused because a BACKGROUND reconnect for
    /// somebody else held the slot, and a capture takes tens of seconds. The user saw
    /// "a login attempt is already running" for an attempt that was never theirs.</para>
    /// </summary>
    private readonly HashSet<Guid> _captureHolders = new();

    /// <summary>How many captures may run together. Chromium is heavy; this is a ceiling, not a queue.</summary>
    private const int MaxConcurrentCaptures = 2;

    /// <summary>Consecutive capture failures across all users; reset by any success.</summary>
    private int _recentCaptureFailures;
    private readonly object _globalGate = new();

    /// <summary>Set when the broker refuses the IP itself; applies to every user.</summary>
    private DateTimeOffset _globalRetryAfterUtc = DateTimeOffset.MinValue;

    private int _globalFailures;

    /// <summary>Never start a second capture while one is running.</summary>
    private DateTimeOffset _lastCaptureStartedUtc = DateTimeOffset.MinValue;

    /// <summary>Floor between captures regardless of how many users want one.</summary>
    private static readonly TimeSpan MinGlobalCaptureInterval = TimeSpan.FromSeconds(20);

    public bool CanAttemptCredentialLogin(Guid userId)
    {
        if (IsCoolingDown(userId))
            return false;

        lock (_globalGate)
        {
            var now = DateTimeOffset.UtcNow;

            // The broker is refusing this IP — trying again on behalf of a different user
            // changes nothing and only deepens the block.
            if (now < _globalRetryAfterUtc)
                return false;

            if (_captureHolders.Count >= MaxConcurrentCaptures)
                return false;

            // The spacing floor applies only while captures are actually FAILING. On a
            // healthy broker it serialised ordinary sign-ins behind each other for twenty
            // seconds apiece, for no reason anyone could see.
            if (_recentCaptureFailures > 0 && now - _lastCaptureStartedUtc < MinGlobalCaptureInterval)
                return false;

            // A second capture for the SAME account is always a duplicate, whoever asked.
            if (!_credentialInFlight.TryAdd(userId, 1))
                return false;

            _captureHolders.Add(userId);
            _lastCaptureStartedUtc = now;
            return true;
        }
    }

    public string DescribeCredentialRefusal(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;

        if (_retryAfterUtc.TryGetValue(userId, out var until) && now < until)
        {
            var seconds = (int)(until - now).TotalSeconds + 1;
            return $"This account's last sign-in was refused. Try again in {seconds}s.";
        }

        if (_credentialInFlight.ContainsKey(userId))
            return "A sign-in for this account is already running. Give it a moment.";

        lock (_globalGate)
        {
            if (now < _globalRetryAfterUtc)
            {
                var seconds = (int)(_globalRetryAfterUtc - now).TotalSeconds + 1;
                return "Binolla is refusing sign-ins from this server right now. "
                       + $"It retries automatically in {seconds}s.";
            }
        }

        return "Too many sign-ins are starting at once. Try again in a few seconds.";
    }

    public void MarkCredentialLoginFailed(Guid userId) => MarkCredentialLoginFailed(userId, false);

    public void MarkCredentialLoginFailed(Guid userId, bool blockedByBroker)
    {
        _credentialInFlight.TryRemove(userId, out _);
        ReleaseGlobalSlot(userId);
        lock (_globalGate)
        {
            _recentCaptureFailures = Math.Min(_recentCaptureFailures + 1, 8);
        }

        var failures = _credentialFailures.AddOrUpdate(userId, 1, (_, n) => n + 1);

        // 30s, 60s, 120s, 240s … capped at 15 minutes. A broker refusing logins is not
        // going to change its mind inside a second, and backing off is what keeps the IP
        // out of a rate-limit ban.
        var baseSeconds = Math.Max(1, _options.FailureCooldownSeconds);
        var factor = Math.Min(1 << Math.Min(failures - 1, 6), 32);
        var delay = Math.Min(baseSeconds * factor, 900);
        _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddSeconds(delay);

        if (!blockedByBroker)
            return;

        lock (_globalGate)
        {
            // An IP-level refusal is not per-account, so pause EVERY user. Escalates the
            // same way: 1, 2, 4 … up to 15 minutes.
            _globalFailures = Math.Min(_globalFailures + 1, 8);
            var globalDelay = Math.Min(60 * (1 << Math.Min(_globalFailures - 1, 4)), 900);
            _globalRetryAfterUtc = DateTimeOffset.UtcNow.AddSeconds(globalDelay);
        }
    }

    private void ReleaseGlobalSlot(Guid userId)
    {
        lock (_globalGate)
        {
            _captureHolders.Remove(userId);
        }
    }

    public void EnsureBackgroundRestore(Guid userId)
    {
        if (!_options.Enabled || IsLive(userId) || _authFailed.ContainsKey(userId))
            return;

        if (IsCoolingDown(userId))
            return;

        if (!_bgRestoreQueued.TryAdd(userId, 1))
            return;

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
            "H123",
            "BinollaSessionRestoreService.EnsureBackgroundRestore",
            "queued",
            new { });
        // #endregion

        _ = Task.Run(async () =>
        {
            try
            {
                await RestoreOneAsync(userId, maxAttempts: Math.Max(1, _options.LazyMaxAttempts), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background session restore ended for user {UserId}", userId);
            }
            finally
            {
                _bgRestoreQueued.TryRemove(userId, out _);
            }
        });
    }

    /// <summary>
    /// Whether a link can be brought back up without the user typing anything: an SSID for
    /// Binolla, a stored credential pair for a gateway broker.
    /// </summary>
    private static bool HasRestorableSecret(Domain.Entities.BinollaLink link) =>
        Brokers.Normalize(link.Broker) == Brokers.Binolla
            ? !string.IsNullOrWhiteSpace(link.EncryptedSsid)
            : !string.IsNullOrWhiteSpace(link.EncryptedBinollaEmail)
              && !string.IsNullOrWhiteSpace(link.EncryptedBinollaPassword);

    /// <summary>
    /// Reconnects one gateway-backed broker session. Single attempt on purpose: the gateway
    /// runs its own throttle and backoff, and a second layer of retries stacked on top is
    /// what turned a refused login into sustained hammering of the broker.
    /// </summary>
    private async Task<bool> RestoreGatewayLinkAsync(
        Guid userId,
        Guid linkId,
        string broker,
        string? emailCipher,
        string? passwordCipher,
        EngineAccount accountType)
    {
        var existing = _brokerSessions.Get(userId, broker);
        if (existing is not null &&
            existing.IsTransportConnected &&
            existing.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(emailCipher) || string.IsNullOrWhiteSpace(passwordCipher))
            return false;

        string email, password;
        try
        {
            email = _protector.Decrypt(emailCipher);
            password = _protector.Decrypt(passwordCipher);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Session restore: {Broker} credential decrypt failed for user {UserId}", broker, userId);
            await MarkLinkDisconnectedAsync(userId, "DECRYPT_FAILED", CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return false;

        try
        {
            await _brokerSessions.GetOrCreateAsync(
                    userId,
                    broker,
                    new BrokerCredentials(
                        Email: email, Password: password, AccountType: accountType),
                    CancellationToken.None)
                .ConfigureAwait(false);

            await TouchLastConnectedAsync(userId, CancellationToken.None).ConfigureAwait(false);
            _authFailed.TryRemove(userId, out _);
            _retryAfterUtc.TryRemove(userId, out _);
            _credentialFailures.TryRemove(userId, out _);

            _logger.LogInformation(
                "Session restore: connected user {UserId} on {Broker} link={LinkId}",
                userId, broker, linkId);
            return true;
        }
        catch (Exception ex)
        {
            // Back off before anyone tries again. Without this there was NO cooldown on
            // this path at all: every status poll queued another restore, each failed in
            // milliseconds, and the retries piled up fast enough to exhaust the gateway's
            // own connect limit — so the person actually signing in was refused too.
            MarkGatewayRestoreFailed(userId);

            // Left Connected deliberately: the link is still valid and the next sweep or the
            // next page load retries. Marking it Disconnected on a transient gateway failure
            // would push a working account back to the login screen.
            _logger.LogWarning(
                ex, "Session restore: {Broker} reconnect failed for user {UserId}", broker, userId);
            return false;
        }
    }

    /// <summary>
    /// Whether this user already has a healthy session — on ANY venue.
    ///
    /// <para>Asking Binolla's manager alone reported every Quotex user as dead, however
    /// healthy their session was. Every access check then queued a restore, and each of
    /// those took the broker's one connect slot — so the person actually signing in was
    /// refused with "a login attempt is already running" for an account that was already
    /// connected.</para>
    /// </summary>
    /// <summary>
    /// Applies the same growing backoff the Binolla path uses, for a gateway broker.
    ///
    /// <para>Shares <see cref="_retryAfterUtc"/> so <c>IsCoolingDown</c> — which every
    /// entry point already checks — holds this user off without a second mechanism to
    /// keep in step.</para>
    /// </summary>
    private void MarkGatewayRestoreFailed(Guid userId)
    {
        var failures = _credentialFailures.AddOrUpdate(userId, 1, (_, n) => n + 1);
        var baseSeconds = Math.Max(1, _options.FailureCooldownSeconds);
        var factor = Math.Min(1 << Math.Min(failures - 1, 6), 32);
        var delay = Math.Min(baseSeconds * factor, 900);
        _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddSeconds(delay);
    }

    private bool IsLive(Guid userId)
    {
        foreach (var broker in Brokers.All)
        {
            var client = broker == Brokers.Binolla
                ? (object?)_sessions.Get(userId.ToString())
                : _brokerSessions.Get(userId, broker);

            if (client is null) continue;

            var (transportUp, lifecycle) = client switch
            {
                IBinollaClient b => (b.IsTransportConnected, b.Lifecycle),
                IBrokerClient g => (g.IsTransportConnected, g.Lifecycle),
                _ => (false, SessionLifecycleState.Disconnected)
            };

            if (transportUp &&
                lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> RestoreOneAsync(Guid userId, int maxAttempts, CancellationToken ct)
    {
        var userGate = _userGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await userGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Another restore may still finish — report live state instead of false NOT_CONNECTED.
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H110",
                "BinollaSessionRestoreService.RestoreOneAsync",
                "gate_wait_canceled",
                new { live = IsLive(userId) });
            // #endregion
            return IsLive(userId);
        }

        try
        {
            if (IsLive(userId))
                return true;

            if (_authFailed.ContainsKey(userId))
                return false;

            if (IsCoolingDown(userId))
                return false;

            string? ciphertext = null;
            string? cookieCipher = null;
            string broker = Brokers.Binolla;
            string? emailCipher = null;
            string? passwordCipher = null;
            EngineAccount gatewayAccountType = EngineAccount.Real;
            Guid linkId = Guid.Empty;
            BinollaLinkStatus linkStatus = BinollaLinkStatus.Disconnected;
            bool approved = false;
            bool pending = false;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var links = scope.ServiceProvider.GetRequiredService<IBinollaLinkRepository>();
                var link = await links.GetByUserIdAsync(userId, CancellationToken.None).ConfigureAwait(false);
                if (link is null)
                    return false;

                linkId = link.Id;
                linkStatus = link.Status;
                approved = link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Approved;
                pending = link.ApprovalStatus == AdminApprovalStatus.Pending;
                ciphertext = link.EncryptedSsid;
                cookieCipher = link.EncryptedCookieHeader;
                broker = Brokers.Normalize(link.Broker);
                emailCipher = link.EncryptedBinollaEmail;
                passwordCipher = link.EncryptedBinollaPassword;
                gatewayAccountType = link.AccountType == BinollaAccountType.Demo
                    ? EngineAccount.Demo
                    : EngineAccount.Real;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session restore: failed loading link for user {UserId}", userId);
                return false;
            }

            // Restore previously-connected links for approved OR pending (market browse).
            // Rejected accounts stay offline.
            var connectedLink = linkStatus == BinollaLinkStatus.Connected && (approved || pending);
            if (!connectedLink)
                return false;

            // Gateway brokers reconnect on their stored credentials, not on an SSID, so they
            // branch off before every check below - all of which are about a captured Binolla
            // session. Without this a Quotex user is logged out by every API restart, holding
            // a link that still says Connected.
            if (broker != Brokers.Binolla)
            {
                return await RestoreGatewayLinkAsync(
                    userId, linkId, broker, emailCipher, passwordCipher, gatewayAccountType)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(ciphertext))
                return false;

            string ssid;
            try
            {
                ssid = _protector.Decrypt(ciphertext);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Session restore: decrypt failed for user {UserId} link={LinkId}; skipping",
                    userId, linkId);
                await MarkLinkDisconnectedAsync(userId, "DECRYPT_FAILED", CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            if (string.IsNullOrWhiteSpace(ssid))
            {
                _logger.LogWarning(
                    "Session restore: empty SSID after decrypt for user {UserId} link={LinkId}; skipping",
                    userId, linkId);
                await MarkLinkDisconnectedAsync(userId, "EMPTY_SSID", CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            var delay = Math.Max(0, _options.InitialDelayMs);
            var maxDelay = Math.Max(delay, _options.MaxDelayMs);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested)
                    return IsLive(userId);

                try
                {
                    // Prefer live in-memory cookies; fall back to encrypted cookies from DB
                    // so API restart can restore without forcing a full credential re-login.
                    var existingSession = _sessions.Get(userId.ToString()) as BinollaSession;
                    var cookieHeader = existingSession?.State.CookieHeader;
                    if (string.IsNullOrWhiteSpace(cookieHeader) && !string.IsNullOrWhiteSpace(cookieCipher))
                    {
                        try { cookieHeader = _protector.Decrypt(cookieCipher); }
                        catch { cookieHeader = null; }
                    }

                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                        "H104",
                        "BinollaSessionRestoreService.RestoreOneAsync",
                        "restore_attempt",
                        new
                        {
                            attempt,
                            hasCookie = !string.IsNullOrWhiteSpace(cookieHeader),
                            cookieFromDb = string.IsNullOrWhiteSpace(existingSession?.State.CookieHeader) &&
                                           !string.IsNullOrWhiteSpace(cookieCipher),
                            cookieLen = cookieHeader?.Length ?? 0,
                            priorLifecycle = existingSession?.Lifecycle.ToString() ?? "None"
                        });
                    // #endregion

                    // Use None for the socket handshake so an 18s access-check CTS cannot
                    // abort auth after sockets are up; gate still serializes per user.
                    var client = await _sessions.GetOrCreateAsync(
                            userId.ToString(),
                            ssid,
                            CancellationToken.None,
                            cookieHeader)
                        .ConfigureAwait(false);
                    _ = client;

                    await TouchLastConnectedAsync(userId, CancellationToken.None).ConfigureAwait(false);
                    _authFailed.TryRemove(userId, out _);
                    _retryAfterUtc.TryRemove(userId, out _);

                    _logger.LogInformation(
                        "Session restore: connected user {UserId} link={LinkId} attempt={Attempt}",
                        userId, linkId, attempt);
                    return true;
                }
                catch (BinollaAuthenticationException ex)
                {
                    // Expired / invalid SSID — try silent credential re-login when stored.
                    _logger.LogWarning(
                        ex,
                        "Session restore: authentication failed for user {UserId} link={LinkId}; trying stored credentials",
                        userId, linkId);

                    var relogged = await TryCredentialReloginAsync(userId, CancellationToken.None).ConfigureAwait(false);
                    if (relogged)
                    {
                        _authFailed.TryRemove(userId, out _);
                        _retryAfterUtc.TryRemove(userId, out _);
                        return true;
                    }

                    _authFailed[userId] = 1;
                    _retryAfterUtc.TryRemove(userId, out _);
                    await MarkLinkDisconnectedAsync(userId, "SSID_EXPIRED", CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                        "H110",
                        "BinollaSessionRestoreService.RestoreOneAsync",
                        "restore_canceled_check_live",
                        new { live = IsLive(userId), attempt });
                    // #endregion
                    return IsLive(userId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Session restore: reconnect failed for user {UserId} link={LinkId} attempt={Attempt}/{Max}",
                        userId, linkId, attempt, maxAttempts);

                    if (attempt >= maxAttempts)
                    {
                        SetFailureCooldown(userId);
                        return IsLive(userId);
                    }

                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return IsLive(userId);
                    }

                    delay = Math.Min(maxDelay, Math.Max(1, delay) * 2);
                }
            }

            return IsLive(userId);
        }
        finally
        {
            userGate.Release();
            // Avoid retaining plaintext beyond this method.
        }
    }

    private async Task<bool> TryCredentialReloginAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var binolla = scope.ServiceProvider.GetRequiredService<BinollaAppService>();
            using (AmbientUserContext.Use(userId))
            {
                _authFailed.TryRemove(userId, out _);
                var result = await binolla.TryReloginFromStoredCredentialsAsync(ct).ConfigureAwait(false);
                var ok = result is { Connected: true };
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "BR1",
                    "BinollaSessionRestoreService.TryCredentialReloginAsync",
                    ok ? "relogin_ok" : "relogin_skip",
                    new { userId = userId.ToString("N")[..8] });
                // #endregion
                return ok;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored-credential relogin failed for user {UserId}", userId);
            return false;
        }
    }

    private async Task TouchLastConnectedAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var links = scope.ServiceProvider.GetRequiredService<IBinollaLinkRepository>();
            var link = await links.GetByUserIdAsync(userId, ct).ConfigureAwait(false);
            if (link is null) return;

            var now = DateTimeOffset.UtcNow;
            link.LastConnectedAt = now;
            link.UpdatedAt = now;
            link.Status = BinollaLinkStatus.Connected;
            await links.UpsertAsync(link, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session restore: failed updating LastConnectedAt for user {UserId}", userId);
        }
    }

    private bool IsCoolingDown(Guid userId)
    {
        if (!_retryAfterUtc.TryGetValue(userId, out var retryAfter))
            return false;

        if (retryAfter > DateTimeOffset.UtcNow)
            return true;

        _retryAfterUtc.TryRemove(userId, out _);
        return false;
    }

    private void SetFailureCooldown(Guid userId)
    {
        _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddSeconds(
            Math.Max(1, _options.FailureCooldownSeconds));
    }

    private async Task MarkLinkDisconnectedAsync(Guid userId, string reason, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var links = scope.ServiceProvider.GetRequiredService<IBinollaLinkRepository>();
            var link = await links.GetByUserIdAsync(userId, ct).ConfigureAwait(false);
            if (link is null) return;

            link.Status = BinollaLinkStatus.Disconnected;
            link.UpdatedAt = DateTimeOffset.UtcNow;
            await links.UpsertAsync(link, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Session restore: marked link disconnected for user {UserId} reason={Reason}",
                userId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session restore: failed marking disconnected for user {UserId}", userId);
        }
    }
}
