using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
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
    /// <summary>
    /// Auth lockout with an expiry. A permanent flag is what left the bot dark
    /// overnight after one dead SSID — restore never tried again until a restart.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _authFailedUntil = new();
    private DateTimeOffset _gatewayDarkSinceUtc;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userGates = new();
    private readonly ConcurrentDictionary<Guid, byte> _bgRestoreQueued = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _retryAfterUtc = new();
    private readonly SemaphoreSlim _quotexRestoreGate = new(1, 1);
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

    private bool IsAuthLocked(Guid userId)
    {
        if (!_authFailedUntil.TryGetValue(userId, out var until))
            return false;
        if (until > DateTimeOffset.UtcNow)
            return true;
        _authFailedUntil.TryRemove(userId, out _);
        return false;
    }

    private void LockAuth(Guid userId, int minutes = 20)
        => _authFailedUntil[userId] = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(minutes, 2, 60));

    private void UnlockAuth(Guid userId) => _authFailedUntil.TryRemove(userId, out _);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunStartupRestoreAsync(_cts.Token));
        _ = Task.Run(() => RunKeepAliveRestoreAsync(_cts.Token));
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

    /// <summary>
    /// Keeps Quotex/Binolla sessions up after the browser is gone. Startup restore is a
    /// one-shot; without this a dropped socket stays dead until the next API restart.
    /// </summary>
    private async Task RunKeepAliveRestoreAsync(CancellationToken ct)
    {
        try
        {
            await _initialDone.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                await WatchGatewayDarkAsync(ct).ConfigureAwait(false);
                await RestoreApprovedSessionsAsync(ct, verbose: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session keep-alive restore wave failed; next pass in 30s");
            }
        }
    }

    /// <summary>
    /// Layer: C# wrappers can look connected while Python has zero sockets.
    /// A 409 cooldown while the gateway is empty is what kept restore off for
    /// 15 minutes. Drop those cooldowns after 45s dark. Auth lockouts keep
    /// their TTL so a PIN/captcha does not relaunch Chromium every wave.
    /// </summary>
    private async Task WatchGatewayDarkAsync(CancellationToken ct)
    {
        var sessions = await _brokerSessions.GatewaySessionCountAsync(ct).ConfigureAwait(false);
        if (sessions < 0)
            return;
        if (sessions > 0)
        {
            _gatewayDarkSinceUtc = DateTimeOffset.MinValue;
            return;
        }

        if (_gatewayDarkSinceUtc == DateTimeOffset.MinValue)
            _gatewayDarkSinceUtc = DateTimeOffset.UtcNow;

        if (DateTimeOffset.UtcNow - _gatewayDarkSinceUtc < TimeSpan.FromSeconds(8))
            return;

        var unlocked = 0;
        foreach (var key in _retryAfterUtc.Keys.ToArray())
        {
            // A 45s pause after a failed sign-in stays. Only a multi-minute
            // hold is a stuck cooldown, and the socket is already dark.
            if (!_retryAfterUtc.TryGetValue(key, out var until) ||
                until <= DateTimeOffset.UtcNow.AddMinutes(2))
                continue;
            if (_retryAfterUtc.TryRemove(key, out _))
                unlocked++;
        }

        if (unlocked > 0)
        {
            _logger.LogWarning(
                "Watchdog: Quotex has been dark for {Seconds}s; cleared {Count} restore cooldown(s)",
                (int)(DateTimeOffset.UtcNow - _gatewayDarkSinceUtc).TotalSeconds,
                unlocked);
        }

        _gatewayDarkSinceUtc = DateTimeOffset.UtcNow;
    }

    public Task RestoreApprovedSessionsAsync(CancellationToken ct = default) =>
        RestoreApprovedSessionsAsync(ct, verbose: true);

    private async Task RestoreApprovedSessionsAsync(CancellationToken ct, bool verbose)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Binolla session restore is disabled");
            return;
        }

        if (CaptureRunning)
        {
            if (verbose)
            {
                _logger.LogInformation("Session restore paused — a user is signing in");
            }
            return;
        }

        try
        {
            await _brokerSessions.RefreshGatewaySessionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway session refresh before restore failed");
        }

        await AdoptLiveGatewaySessionsAsync(ct, verbose).ConfigureAwait(false);

        IReadOnlyList<(Guid UserId, Guid LinkId, string Broker)> targets;
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
                .Select(l => (UserId: l.UserId, LinkId: l.Id, Broker: Brokers.Normalize(l.Broker)))
                .Where(t => !IsLive(t.UserId)
                            && !IsCoolingDown(t.UserId)
                            && !IsCapturing(t.UserId)
                            && !IsAuthLocked(t.UserId))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Binolla links for session restore");
            return;
        }

        if (targets.Count == 0)
        {
            if (verbose)
            {
                _logger.LogInformation(
                    "Session restore wave finished; nothing to reconnect; activeSessions={Binolla} gateway={Gateway}",
                    _sessions.ActiveSessionCount, _brokerSessions.ActiveSessionCount);
            }
            return;
        }

        if (verbose)
        {
            _logger.LogInformation(
                "Session restore starting for {Count} approved/pending linked user(s); parallelism={Parallelism}",
                targets.Count, Math.Max(1, _options.MaxDegreeOfParallelism));
        }

        var binolla = targets.Where(t => t.Broker == Brokers.Binolla).ToList();
        var gateway = targets.Where(t => t.Broker != Brokers.Binolla).ToList();

        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxDegreeOfParallelism));
        var tasks = binolla.Select(async item =>
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

        // Restore every missing account sequentially, including when others are live.
        foreach (var item in gateway)
        {
            if (ct.IsCancellationRequested) break;
            await RestoreOneAsync(item.UserId, maxAttempts: _options.MaxAttempts, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Session restore wave finished; activeSessions={Binolla} gateway={Gateway}",
            _sessions.ActiveSessionCount, _brokerSessions.ActiveSessionCount);
    }

    public async Task<bool> TryRestoreUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return IsLive(userId);

        if (IsLive(userId))
            return true;

        if (IsAuthLocked(userId))
            return false;

        if (IsCoolingDown(userId))
            return false;

        return await RestoreOneAsync(userId, maxAttempts: Math.Max(1, _options.LazyMaxAttempts), ct)
            .ConfigureAwait(false);
    }

    public Task<bool> ClaimInteractiveAsync(Guid userId, CancellationToken ct = default)
    {
        // The person at the keyboard is switching Demo/Real or placing a trade.
        // Background restore must not steal the socket; this call may.
        UnlockAuth(userId);
        _retryAfterUtc.TryRemove(userId, out _);
        // Never takeover. A second handshake disconnects the one socket that feeds
        // candles and pairs to every user.
        return RestoreOneAsync(userId, maxAttempts: 1, ct, takeover: false);
    }

    public void ClearAuthFailure(Guid userId)
    {
        UnlockAuth(userId);
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
        return true;
    }

    public string DescribeCredentialRefusal(Guid userId)
    {
        return "Sign-in is processing. Please wait a moment.";
    }

    public void MarkCredentialLoginFailed(Guid userId) => MarkCredentialLoginFailed(userId, false);

    public void MarkCredentialLoginFailed(Guid userId, bool blockedByBroker)
    {
        _credentialInFlight.TryRemove(userId, out _);
        ReleaseGlobalSlot(userId);
        _retryAfterUtc.TryRemove(userId, out _);
        _credentialFailures.TryRemove(userId, out _);
    }

    private void ReleaseGlobalSlot(Guid userId)
    {
        lock (_globalGate)
        {
            _captureHolders.Remove(userId);
        }
    }

    public async Task<bool> EnsureUserLiveAsync(
        Guid userId, CancellationToken ct = default, bool waitForRestore = false)
    {
        if (IsLive(userId))
            return true;

        if (await TryAdoptFreshAsync(userId, ct).ConfigureAwait(false))
            return true;

        if (!waitForRestore)
        {
            EnsureBackgroundRestore(userId);
            return IsLive(userId);
        }

        var gateway = await ReadGatewayCountAsync(ct).ConfigureAwait(false);
        if (gateway < 0)
        {
            // Health did not answer. Do not open a handshake we cannot see.
            EnsureBackgroundRestore(userId);
            return IsLive(userId);
        }

        // This user's session is missing, regardless of other connected accounts.
        // Keep the restore running if the HTTP request stops waiting.
        var restore = TryRestoreUserAsync(userId, CancellationToken.None);
        try
        {
            var finished = await Task.WhenAny(
                    restore, Task.Delay(TimeSpan.FromSeconds(35), ct))
                .ConfigureAwait(false);
            if (finished == restore)
                return await restore.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return IsLive(userId);
        }

        return IsLive(userId);
    }

    private async Task<bool> TryAdoptFreshAsync(Guid userId, CancellationToken ct)
    {
        foreach (var broker in Brokers.All)
        {
            if (string.Equals(broker, Brokers.Binolla, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (await _brokerSessions.TryAdoptAsync(userId, broker, ct, fresh: true)
                        .ConfigureAwait(false) &&
                    IsLive(userId))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Adopt before a user action failed for user {UserId}", userId);
            }
        }

        return IsLive(userId);
    }

    private async Task<int> ReadGatewayCountAsync(CancellationToken ct)
    {
        try
        {
            return await _brokerSessions.GatewaySessionCountAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return -1;
        }
    }

    public void EnsureBackgroundRestore(Guid userId)
    {
        if (!_options.Enabled || IsLive(userId) || IsAuthLocked(userId))
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
    /// Whether a link can be brought back up without the user typing anything: a captured
    /// session, or - for a gateway broker that can connect without one - a credential pair.
    /// </summary>
    private static bool HasRestorableSecret(Domain.Entities.BinollaLink link) =>
        Brokers.Normalize(link.Broker) == Brokers.Binolla
            ? !string.IsNullOrWhiteSpace(link.EncryptedSsid)
            : !string.IsNullOrWhiteSpace(link.EncryptedSsid)
              || (!string.IsNullOrWhiteSpace(link.EncryptedBinollaEmail)
                  && !string.IsNullOrWhiteSpace(link.EncryptedBinollaPassword));

    /// <summary>
    /// Reconnects one gateway-backed broker session. Single attempt on purpose: the gateway
    /// runs its own throttle and backoff, and a second layer of retries stacked on top is
    /// what turned a refused login into sustained hammering of the broker.
    /// </summary>
    private async Task<bool> RestoreGatewayLinkAsync(
        Guid userId,
        Guid linkId,
        string broker,
        string? ssidCipher,
        string? emailCipher,
        string? passwordCipher,
        EngineAccount accountType,
        bool takeover = false)
    {
        var existing = _brokerSessions.Get(userId, broker);
        if (existing is not null &&
            existing.IsTransportConnected &&
            existing.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return true;
        }

        // API restarts wipe C# wrappers. The gateway still holds the socket — adopt it
        // before any handshake so opening the server never disconnects a live account.
        if (await _brokerSessions.TryAdoptAsync(userId, broker, CancellationToken.None).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Session restore: adopted live {Broker} session for user {UserId} link={LinkId}",
                broker, userId, linkId);
            await TouchLastConnectedAsync(userId, CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        // Space browser logins while retaining a separate socket for each account.
        // A healthy session owned by somebody else must not block this user's restore.
        if (broker == Brokers.Quotex)
            await _quotexRestoreGate.WaitAsync().ConfigureAwait(false);
        try
        {
        // The session captured at sign-in comes first. On Quotex it is the only thing that
        // works: its Cloudflare refuses the WebSocket upgrade for a connection that did not
        // actually sign in through a browser, so a restore built from email and password
        // alone reconnects into a session that dies on first use. Credentials stay as the
        // fallback for a venue that does not need the browser.
        string? ssid = null;
        if (!string.IsNullOrWhiteSpace(ssidCipher))
        {
            try
            {
                ssid = _protector.Decrypt(ssidCipher);
            }
            catch (Exception ex)
            {
                // Not fatal on its own - the credentials below may still work - but the key
                // having changed is worth saying out loud rather than silently degrading.
                _logger.LogWarning(
                    ex, "Session restore: {Broker} session decrypt failed for user {UserId}", broker, userId);
            }
        }

        string? email = null, password = null;
        if (!string.IsNullOrWhiteSpace(emailCipher) && !string.IsNullOrWhiteSpace(passwordCipher))
        {
            try
            {
                email = _protector.Decrypt(emailCipher);
                password = _protector.Decrypt(passwordCipher);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Session restore: {Broker} credential decrypt failed for user {UserId}", broker, userId);
                if (string.IsNullOrWhiteSpace(ssid))
                {
                    await MarkLinkDisconnectedAsync(userId, "DECRYPT_FAILED", CancellationToken.None)
                        .ConfigureAwait(false);
                    return false;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(ssid))
        {
            if (broker == Brokers.Quotex)
            {
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                    return false;
                // An expired token may already have been cleared. Use the same
                // guarded browser-login path as expiry recovery, instead of
                // silently skipping this account on every subsequent restore wave.
                var relogged = await TryCredentialReloginAsync(userId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!relogged)
                    _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddSeconds(45);
                return relogged;
            }

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                return false;
        }

        try
        {
            await _brokerSessions.GetOrCreateAsync(
                    userId,
                    broker,
                    new BrokerCredentials(
                        Ssid: ssid,
                        Email: email,
                        Password: password,
                        AccountType: accountType,
                        Takeover: takeover),
                    CancellationToken.None)
                .ConfigureAwait(false);

            await TouchLastConnectedAsync(userId, CancellationToken.None).ConfigureAwait(false);
            UnlockAuth(userId);
            _retryAfterUtc.TryRemove(userId, out _);
            _credentialFailures.TryRemove(userId, out _);

            _logger.LogInformation(
                "Session restore: connected user {UserId} on {Broker} link={LinkId}",
                userId, broker, linkId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Session restore: {Broker} reconnect failed for user {UserId}", broker, userId);

            if (broker == Brokers.Binolla)
            {
                var relogged = await TryCredentialReloginAsync(userId, CancellationToken.None).ConfigureAwait(false);
                if (relogged)
                {
                    UnlockAuth(userId);
                    _retryAfterUtc.TryRemove(userId, out _);
                    _credentialFailures.TryRemove(userId, out _);
                    return true;
                }
            }

            if (broker == Brokers.Quotex)
            {
                if (ex is ApiException api && api.StatusCode is 409 or 503 or 504)
                {
                    // Only a live other-account 409 should wait 15 minutes.
                    // NOT_CONNECTED is also HTTP 409 — treating it as "another
                    // account" locked every user for 15 minutes while sessions=0.
                    var anotherAccount = api.StatusCode == 409 &&
                        api.Message.Contains("Another Quotex account", StringComparison.OrdinalIgnoreCase);
                    var expiredSsid = api.Message.Contains("expired", StringComparison.OrdinalIgnoreCase)
                        || api.Message.Contains("SSID is invalid", StringComparison.OrdinalIgnoreCase);
                    if (!expiredSsid)
                    {
                        MarkGatewayRestoreFailed(
                            userId,
                            authFailed: false,
                            gatewayNotReady: !anotherAccount,
                            anotherAccount: anotherAccount);
                        return IsLive(userId);
                    }
                }

                // Stored SSID is dead. Sign in with the saved email/password and
                // replace it — waiting for the customer to click Connect is what
                // left analysis and trades off after every token expiry.
                UnlockAuth(userId);
                _retryAfterUtc.TryRemove(userId, out _);
                await ClearExpiredGatewaySsidAsync(userId, CancellationToken.None).ConfigureAwait(false);
                _logger.LogWarning(
                    "Session restore: Quotex SSID dead for user {UserId}; capturing a fresh session",
                    userId);
                var relogged = await TryCredentialReloginAsync(userId, CancellationToken.None).ConfigureAwait(false);
                if (relogged)
                {
                    UnlockAuth(userId);
                    _retryAfterUtc.TryRemove(userId, out _);
                    _credentialFailures.TryRemove(userId, out _);
                    return true;
                }

                // A failed capture must not park the account for 20 minutes.
                // The socket is down; the next pass signs in again from the
                // credentials already stored on the server.
                _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddSeconds(45);
                return false;
            }

            MarkGatewayRestoreFailed(userId, authFailed: true);
            return false;
        }
        }
        finally
        {
            if (broker == Brokers.Quotex)
                _quotexRestoreGate.Release();
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
    private void MarkGatewayRestoreFailed(
        Guid userId,
        bool authFailed = false,
        bool gatewayNotReady = false,
        bool anotherAccount = false)
    {
        if (gatewayNotReady)
        {
            _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddSeconds(8);
            return;
        }

        if (anotherAccount)
        {
            // Another live socket is holding the vendor. Retrying every 2 minutes
            // was still a 409 storm. Wait until that session actually drops.
            _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddMinutes(15);
            return;
        }

        var failures = _credentialFailures.AddOrUpdate(userId, 1, (_, n) => n + 1);
        var baseSeconds = authFailed ? 300 : Math.Max(60, _options.FailureCooldownSeconds);
        var factor = Math.Min(1 << Math.Min(failures - 1, 4), 8);
        var delay = Math.Min(baseSeconds * factor, 900);
        _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddSeconds(delay);
    }

    /// <summary>
    /// Bind C# wrappers to sockets the gateway already holds. No POST /sessions/connect,
    /// so opening the API never races three handshakes and never steals a live account.
    /// </summary>
    private async Task AdoptLiveGatewaySessionsAsync(CancellationToken ct, bool verbose)
    {
        IReadOnlyList<(Guid UserId, string Broker)> candidates;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var links = scope.ServiceProvider.GetRequiredService<IBinollaLinkRepository>();
            var all = await links.ListAsync(approvalStatus: null, ct).ConfigureAwait(false);
            candidates = all
                .Where(l =>
                    l.Status == BinollaLinkStatus.Connected &&
                    l.ApprovalStatus != AdminApprovalStatus.Rejected)
                .Select(l => (UserId: l.UserId, Broker: Brokers.Normalize(l.Broker)))
                .Where(t => t.Broker != Brokers.Binolla && !IsLive(t.UserId))
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session adopt: failed to load linked users");
            return;
        }

        if (candidates.Count == 0)
            return;

        if (verbose)
            await WaitForGatewayAsync(ct, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        else if (!await _brokerSessions.IsGatewayReachableAsync(ct).ConfigureAwait(false))
            return;

        var adopted = 0;
        await Task.WhenAll(candidates.Select(async item =>
        {
            try
            {
                if (await _brokerSessions.TryAdoptAsync(item.UserId, item.Broker, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref adopted);
                    _logger.LogInformation(
                        "Session adopt: attached live {Broker} session for user {UserId} without a handshake",
                        item.Broker, item.UserId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex, "Session adopt skipped for user {UserId} on {Broker}", item.UserId, item.Broker);
            }
        })).ConfigureAwait(false);

        if (verbose || adopted > 0)
        {
            _logger.LogInformation(
                "Session adopt finished; attached={Adopted} candidates={Candidates} gateway={Gateway}",
                adopted, candidates.Count, _brokerSessions.ActiveSessionCount);
        }
    }

    private async Task WaitForGatewayAsync(CancellationToken ct, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            if (await _brokerSessions.IsGatewayReachableAsync(ct).ConfigureAwait(false))
                return;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogWarning(
            "Broker gateway was not reachable within {Seconds}s — restore will adopt when it comes up",
            (int)budget.TotalSeconds);
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

    private async Task<bool> RestoreOneAsync(
        Guid userId, int maxAttempts, CancellationToken ct, bool takeover = false)
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

            if (!takeover && IsAuthLocked(userId))
                return false;

            if (!takeover && IsCoolingDown(userId))
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

            // Gateway brokers branch off before every check below - all of which are about a
            // captured Binolla session. Without this a Quotex user is logged out by every API
            // restart, holding a link that still says Connected.
            if (broker != Brokers.Binolla)
            {
                return await RestoreGatewayLinkAsync(
                    userId, linkId, broker, ciphertext, emailCipher, passwordCipher, gatewayAccountType, takeover)
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
                    UnlockAuth(userId);
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
                        UnlockAuth(userId);
                        _retryAfterUtc.TryRemove(userId, out _);
                        return true;
                    }

                    LockAuth(userId);
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
                UnlockAuth(userId);
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

    private async Task ClearExpiredGatewaySsidAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var links = scope.ServiceProvider.GetRequiredService<IBinollaLinkRepository>();
            var link = await links.GetByUserIdAsync(userId, ct).ConfigureAwait(false);
            if (link is null || string.IsNullOrWhiteSpace(link.EncryptedSsid))
                return;

            link.EncryptedSsid = string.Empty;
            link.EncryptedCookieHeader = null;
            link.UpdatedAt = DateTimeOffset.UtcNow;
            await links.UpsertAsync(link, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Session restore: cleared expired gateway SSID for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session restore: failed clearing expired SSID for user {UserId}", userId);
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
        if (_retryAfterUtc.TryGetValue(userId, out var until) && until > DateTimeOffset.UtcNow)
            return true;
        return false;
    }

    private void SetFailureCooldown(Guid userId)
    {
        _retryAfterUtc[userId] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, _options.FailureCooldownSeconds));
    }

    public void MarkCaptureStarted(Guid userId)
    {
        lock (_globalGate)
        {
            _captureHolders.Add(userId);
        }
    }

    public void MarkCaptureEnded(Guid userId)
    {
        ReleaseGlobalSlot(userId);
        _credentialInFlight.TryRemove(userId, out _);
    }

    private bool IsCapturing(Guid userId)
    {
        lock (_globalGate)
        {
            return _captureHolders.Contains(userId);
        }
    }

    private bool CaptureRunning
    {
        get
        {
            lock (_globalGate)
            {
                return _captureHolders.Count > 0;
            }
        }
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
