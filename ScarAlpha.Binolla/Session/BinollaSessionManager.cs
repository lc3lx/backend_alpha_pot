using System.Collections.Concurrent;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;
using ScarAlpha.Binolla.Transport;

namespace ScarAlpha.Binolla.Session;

/// <summary>
/// In-process multi-user session manager. No Redis — later milestones own distribution.
/// </summary>
public sealed class BinollaSessionManager : IBinollaSessionManager
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions =
        new(StringComparer.Ordinal);

    private readonly BinollaSessionManagerOptions _options;
    private readonly WebSocketTransportFactory _transportFactory;
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private readonly CancellationTokenSource _idleCts = new();
    private readonly Task _idleLoop;
    private int _disposed;

    public BinollaSessionManager(
        BinollaSessionManagerOptions? options = null,
        WebSocketTransportFactory? transportFactory = null)
    {
        _options = options ?? new BinollaSessionManagerOptions();
        _transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        _idleLoop = Task.Run(() => IdleWatchAsync(_idleCts.Token));
    }

    public int ActiveSessionCount => _sessions.Count;

    public async Task<IBinollaClient> GetOrCreateAsync(
        string userId,
        string ssid,
        CancellationToken cancellationToken = default,
        string? cookieHeader = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(ssid))
            throw new BinollaAuthenticationException("SSID is required.");

        if (_sessions.TryGetValue(userId, out var existing))
        {
            existing.Session.State.Touch();
            await EnsureSessionUsesSsidAsync(existing.Session, ssid, cancellationToken, cookieHeader)
                .ConfigureAwait(false);
            return existing.Session;
        }

        BinollaSession session;
        SessionEntry entry;
        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(userId, out existing))
            {
                existing.Session.State.Touch();
                await EnsureSessionUsesSsidAsync(existing.Session, ssid, cancellationToken, cookieHeader)
                    .ConfigureAwait(false);
                return existing.Session;
            }

            if (_sessions.Count >= _options.MaxConcurrentSessions)
                throw new BinollaException(
                    $"Max concurrent sessions reached ({_options.MaxConcurrentSessions}).");

            session = new BinollaSession(userId, _options, _transportFactory);
            entry = new SessionEntry(session);
            if (!_sessions.TryAdd(userId, entry))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                var raced = _sessions[userId].Session;
                await EnsureSessionUsesSsidAsync(raced, ssid, cancellationToken, cookieHeader)
                    .ConfigureAwait(false);
                return raced;
            }

        }
        finally
        {
            _createGate.Release();
        }

        // Do not hold the global creation gate during a remote WebSocket handshake.
        // Each session has its own lifecycle lock, so users can authenticate in parallel
        // without allowing duplicate connections for the same user.
        await session.ConnectAsync(ssid, cancellationToken, cookieHeader).ConfigureAwait(false);
        return session;
    }

    /// <summary>
    /// If the live session is down OR the SSID changed, reconnect with the provided SSID
    /// so DB EncryptedSsid and live session stay aligned.
    /// </summary>
    private static async Task EnsureSessionUsesSsidAsync(
        BinollaSession session,
        string ssid,
        CancellationToken cancellationToken,
        string? cookieHeader = null)
    {
        if (!string.IsNullOrWhiteSpace(cookieHeader))
            session.State.CookieHeader = cookieHeader.Trim();

        // Never tear down an in-flight connect/reauth — that caused post-login SessionExpired storms.
        if (session.Lifecycle is SessionLifecycleState.Connecting or SessionLifecycleState.Reconnecting)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H105", "BinollaSessionManager.EnsureSessionUsesSsid", "wait_connecting", new
            {
                lifecycle = session.Lifecycle.ToString(),
                hasCookie = !string.IsNullOrWhiteSpace(session.State.CookieHeader)
            });
            // #endregion
            await session.WaitUntilNotConnectingAsync(cancellationToken).ConfigureAwait(false);
        }

        var lifecycle = session.Lifecycle;
        var transportDead = !session.IsTransportConnected;
        var needsLifecycleReconnect = transportDead
            || lifecycle is SessionLifecycleState.Disconnected
                or SessionLifecycleState.Faulted
                or SessionLifecycleState.AuthenticationFailed
                or SessionLifecycleState.SessionExpired;

        var ssidChanged = !string.Equals(session.State.Ssid, ssid, StringComparison.Ordinal);

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.LoginTrace.Write("H81", "BinollaSessionManager.EnsureSessionUsesSsid", "check", new
        {
            lifecycle = lifecycle.ToString(),
            transportDead,
            ssidChanged,
            needsLifecycleReconnect,
            hasCookie = !string.IsNullOrWhiteSpace(session.State.CookieHeader)
        });
        // #endregion

        if (!needsLifecycleReconnect && !ssidChanged)
            return;

        if (!needsLifecycleReconnect && ssidChanged)
            await session.DisconnectAsync(cancellationToken).ConfigureAwait(false);

        // Force a clean reconnect when the previous socket is dead but Lifecycle lied.
        if (transportDead && lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
            await session.DisconnectAsync(cancellationToken).ConfigureAwait(false);

        await session.ConnectAsync(ssid, cancellationToken, cookieHeader).ConfigureAwait(false);
    }

    public IBinollaClient? Get(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        return _sessions.TryGetValue(userId, out var entry) ? entry.Session : null;
    }

    public async Task RemoveAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return;

        if (_sessions.TryRemove(userId, out var entry))
        {
            try
            {
                await entry.Session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await entry.Session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task DisconnectAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(userId, out var entry))
            await entry.Session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try { _idleCts.Cancel(); } catch { /* ignore */ }

        try { await _idleLoop.ConfigureAwait(false); } catch { /* ignore */ }

        foreach (var key in _sessions.Keys.ToArray())
        {
            await RemoveAsync(key).ConfigureAwait(false);
        }

        _idleCts.Dispose();
        _createGate.Dispose();
    }

    private async Task IdleWatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                foreach (var kvp in _sessions)
                {
                    if (now - kvp.Value.Session.State.LastActivityUtc > _options.IdleTimeout)
                        await RemoveAsync(kvp.Key, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // keep loop alive
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            throw new ObjectDisposedException(nameof(BinollaSessionManager));
    }

    private sealed class SessionEntry
    {
        public SessionEntry(BinollaSession session) => Session = session;
        public BinollaSession Session { get; }
    }
}
