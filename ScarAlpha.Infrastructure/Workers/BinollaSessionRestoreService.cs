using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
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
            var approved = await links.ListAsync(AdminApprovalStatus.Approved, ct).ConfigureAwait(false);
            targets = approved
                .Where(l =>
                    l.AdminApproved &&
                    l.ApprovalStatus == AdminApprovalStatus.Approved &&
                    l.Status == BinollaLinkStatus.Connected &&
                    !string.IsNullOrWhiteSpace(l.EncryptedSsid))
                .Select(l => (l.UserId, l.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load approved Binolla links for session restore");
            return;
        }

        _logger.LogInformation(
            "Binolla session restore starting for {Count} approved linked user(s); parallelism={Parallelism}",
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

        return await RestoreOneAsync(userId, maxAttempts: Math.Max(1, _options.LazyMaxAttempts), ct)
            .ConfigureAwait(false);
    }

    public void ClearAuthFailure(Guid userId) => _authFailed.TryRemove(userId, out _);

    private bool IsLive(Guid userId)
    {
        var client = _sessions.Get(userId.ToString());
        return client is not null &&
               client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected;
    }

    private async Task<bool> RestoreOneAsync(Guid userId, int maxAttempts, CancellationToken ct)
    {
        var userGate = _userGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await userGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsLive(userId))
                return true;

            if (_authFailed.ContainsKey(userId))
                return false;

            string? ciphertext = null;
            Guid linkId = Guid.Empty;
            BinollaLinkStatus linkStatus = BinollaLinkStatus.Disconnected;
            bool approved = false;
            bool pending = false;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var links = scope.ServiceProvider.GetRequiredService<IBinollaLinkRepository>();
                var link = await links.GetByUserIdAsync(userId, ct).ConfigureAwait(false);
                if (link is null)
                    return false;

                linkId = link.Id;
                linkStatus = link.Status;
                approved = link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Approved;
                pending = link.ApprovalStatus == AdminApprovalStatus.Pending;
                ciphertext = link.EncryptedSsid;
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
                await MarkLinkDisconnectedAsync(userId, "DECRYPT_FAILED", ct).ConfigureAwait(false);
                return false;
            }

            if (string.IsNullOrWhiteSpace(ssid))
            {
                _logger.LogWarning(
                    "Session restore: empty SSID after decrypt for user {UserId} link={LinkId}; skipping",
                    userId, linkId);
                await MarkLinkDisconnectedAsync(userId, "EMPTY_SSID", ct).ConfigureAwait(false);
                return false;
            }

            var delay = Math.Max(0, _options.InitialDelayMs);
            var maxDelay = Math.Max(delay, _options.MaxDelayMs);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var client = await _sessions.GetOrCreateAsync(userId.ToString(), ssid, ct)
                        .ConfigureAwait(false);
                    await client.ChangeAccountAsync(EngineAccount.Demo, ct).ConfigureAwait(false);

                    await TouchLastConnectedAsync(userId, ct).ConfigureAwait(false);
                    _authFailed.TryRemove(userId, out _);

                    _logger.LogInformation(
                        "Session restore: connected user {UserId} link={LinkId} attempt={Attempt}",
                        userId, linkId, attempt);
                    return true;
                }
                catch (BinollaAuthenticationException ex)
                {
                    // Expired / invalid SSID — do not retry storm; mark and skip.
                    _authFailed[userId] = 1;
                    _logger.LogWarning(
                        ex,
                        "Session restore: authentication failed for user {UserId} link={LinkId} (SSID invalid/expired); skipping further attempts",
                        userId, linkId);
                    await MarkLinkDisconnectedAsync(userId, "SSID_EXPIRED", ct).ConfigureAwait(false);
                    return false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Session restore: reconnect failed for user {UserId} link={LinkId} attempt={Attempt}/{Max}",
                        userId, linkId, attempt, maxAttempts);

                    if (attempt >= maxAttempts)
                        return false;

                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }

                    delay = Math.Min(maxDelay, Math.Max(1, delay) * 2);
                }
            }

            return false;
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
