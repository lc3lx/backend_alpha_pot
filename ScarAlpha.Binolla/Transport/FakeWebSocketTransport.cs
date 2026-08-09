using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ScarAlpha.Binolla.Transport;

/// <summary>
/// In-memory duplex transport for multi-session isolation tests (no network).
/// Server-side automation is attached via <see cref="OnClientMessage"/>.
/// </summary>
public sealed class FakeWebSocketTransport : IWebSocketTransport
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private int _connected;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    public bool IsConnected => Interlocked.CompareExchange(ref _connected, 0, 0) == 1;

    public event Action<string>? TextMessageReceived;
    public event Action<Exception?>? Closed;

    /// <summary>Invoked when the "client" sends a message (for simulate server).</summary>
    public event Func<string, Task>? OnClientMessage;

    public string LastConnectedUri { get; private set; } = string.Empty;
    public ConcurrentBag<string> SentMessages { get; } = new();

    public Task ConnectAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastConnectedUri = uri.ToString();
        Interlocked.Exchange(ref _connected, 1);
        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Fake transport not connected.");

        cancellationToken.ThrowIfCancellationRequested();
        SentMessages.Add(message);

        if (OnClientMessage is not null)
            await OnClientMessage(message).ConfigureAwait(false);
    }

    /// <summary>Inject a message as if received from Binolla.</summary>
    public void InjectServerMessage(string message)
    {
        if (!IsConnected)
            return;

        _inbound.Writer.TryWrite(message);
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        await ShutdownAsync(null).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(null).ConfigureAwait(false);
    }

    public async Task SimulateDropAsync(Exception? error = null)
    {
        await ShutdownAsync(error ?? new Exception("Simulated connection drop")).ConfigureAwait(false);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in _inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                TextMessageReceived?.Invoke(msg);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            await ShutdownAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task ShutdownAsync(Exception? error)
    {
        if (Interlocked.Exchange(ref _connected, 0) == 0 && _pumpCts is null)
            return;

        try { _pumpCts?.Cancel(); } catch { /* ignore */ }
        _inbound.Writer.TryComplete();

        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); } catch { /* ignore */ }
        }

        _pumpCts?.Dispose();
        _pumpCts = null;
        _pumpTask = null;
        Closed?.Invoke(error);
    }
}
