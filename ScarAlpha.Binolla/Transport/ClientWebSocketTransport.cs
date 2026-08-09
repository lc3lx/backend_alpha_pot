using System.Net.WebSockets;
using System.Text;

namespace ScarAlpha.Binolla.Transport;

/// <summary>
/// Production ClientWebSocket transport. Headers match BinollaApiDotNetPro.
/// </summary>
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private readonly object _gate = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private int _connected;

    public bool IsConnected => Interlocked.CompareExchange(ref _connected, 0, 0) == 1
                               && _socket?.State == WebSocketState.Open;

    public event Action<string>? TextMessageReceived;
    public event Action<Exception?>? Closed;

    public async Task ConnectAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        await CloseInternalAsync(CancellationToken.None).ConfigureAwait(false);

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        if (headers != null)
        {
            foreach (var h in headers)
                socket.Options.SetRequestHeader(h.Key, h.Value);
        }

        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _socket = socket;
            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _connected, 1);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);
        }
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        ClientWebSocket socket;
        lock (_gate)
        {
            socket = _socket ?? throw new InvalidOperationException("WebSocket is not connected.");
            if (socket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not open.");
        }

        var buffer = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(buffer, WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        await CloseInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 100];
        Exception? error = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ClientWebSocket? socket;
                lock (_gate) socket = _socket;
                if (socket is null || socket.State != WebSocketState.Open)
                    break;

                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (!string.IsNullOrWhiteSpace(text))
                    TextMessageReceived?.Invoke(text);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // expected
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Interlocked.Exchange(ref _connected, 0);
            Closed?.Invoke(error);
        }
    }

    private async Task CloseInternalAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket? socket;
        CancellationTokenSource? cts;
        Task? receiveTask;

        lock (_gate)
        {
            socket = _socket;
            cts = _receiveCts;
            receiveTask = _receiveTask;
            _socket = null;
            _receiveCts = null;
            _receiveTask = null;
            Interlocked.Exchange(ref _connected, 0);
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore close errors
            }

            socket.Dispose();
        }

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseInternalAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
