using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScarAlpha.Application.Abstractions;
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
        ISecretProtector protector,
        IOptions<BinollaSessionRestoreOptions> options,
        ILogger<BinollaSessionRestoreService> logger)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
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
                    !string.IsNullOrWhiteSpace(l.EncryptedSsid) &&
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

    private bool IsLive(Guid userId)
    {
        var client = _sessions.Get(userId.ToString());
        return client is not null &&
               client.IsTransportConnected &&
               client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session restore: failed loading link for user {UserId}", userId);
                return false;
            }

            // Restore previously-connected links for approved OR pending (market browse).
            // Rejected accounts stay offline.
            var eligible = linkStatus == BinollaLinkStatus.Connected &&
                           !string.IsNullOrWhiteSpace(ciphertext) &&
                           (approved || pending);
            if (!eligible)
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
                    // Expired / invalid SSID — do not retry storm; mark and skip.
                    _authFailed[userId] = 1;
                    _retryAfterUtc.TryRemove(userId, out _);
                    _logger.LogWarning(
                        ex,
                        "Session restore: authentication failed for user {UserId} link={LinkId} (SSID invalid/expired); skipping further attempts",
                        userId, linkId);
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
