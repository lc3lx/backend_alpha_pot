using System.Net.WebSockets;
using System.Text;
using ScarAlpha.Binolla.Diagnostics;

namespace ScarAlpha.Binolla.Transport;

/// <summary>
/// Production ClientWebSocket transport. Headers match BinollaApiDotNetPro.
/// Reassembles fragmented text/binary frames before dispatch (Engine.IO packets must be whole).
/// </summary>
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private readonly object _gate = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private int _connected;
    private int _frameCount;

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

        string? cookieHeader = null;
        if (headers != null)
        {
            foreach (var h in headers)
            {
                if (string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(h.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    cookieHeader = h.Value;
                    continue;
                }

                try
                {
                    socket.Options.SetRequestHeader(h.Key, h.Value);
                }
                catch
                {
                    // Restricted / invalid header — skip
                }
            }
        }

        // Prefer CookieContainer over raw Cookie header (raw Cookie often breaks/blackholes Engine.IO on Linux).
        ApplyCookies(socket, uri, cookieHeader);

        ProtocolTrace.Write("H17", "ClientWebSocketTransport.ConnectAsync", "ws_handshake_start", new
        {
            host = uri.Host,
            hasCookieContainer = socket.Options.Cookies is not null,
            cookieLen = cookieHeader?.Length ?? 0
        });

        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _socket = socket;
            // Independent CTS — must NOT die when the connect caller's token is replaced/completed.
            _receiveCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _connected, 1);
            Interlocked.Exchange(ref _frameCount, 0);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);
        }

        ProtocolTrace.Write("H17", "ClientWebSocketTransport.ConnectAsync", "ws_handshake_ok", new
        {
            state = socket.State.ToString()
        });
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
        var buffer = new byte[1024 * 64];
        using var messageBuffer = new MemoryStream();
        Exception? error = null;
        WebSocketCloseStatus? closeStatus = null;

        ProtocolTrace.Write("H17", "ClientWebSocketTransport.ReceiveLoopAsync", "receive_loop_start");

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
                {
                    closeStatus = result.CloseStatus;
                    ProtocolTrace.Write("H17", "ClientWebSocketTransport.ReceiveLoopAsync", "ws_close_frame", new
                    {
                        status = result.CloseStatus?.ToString(),
                        descLen = result.CloseStatusDescription?.Length ?? 0
                    });
                    break;
                }

                if (result.MessageType is WebSocketMessageType.Text or WebSocketMessageType.Binary)
                {
                    messageBuffer.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;

                    var text = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                    messageBuffer.SetLength(0);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var n = Interlocked.Increment(ref _frameCount);
                    if (n <= 3)
                    {
                        ProtocolTrace.Write("H17", "ClientWebSocketTransport.ReceiveLoopAsync", "ws_frame", new
                        {
                            n,
                            kind = result.MessageType.ToString(),
                            len = text.Length,
                            prefix = text.Length <= 24 ? text : text[..24]
                        });
                    }

                    TextMessageReceived?.Invoke(text);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ProtocolTrace.Write("H17", "ClientWebSocketTransport.ReceiveLoopAsync", "receive_canceled");
        }
        catch (Exception ex)
        {
            error = ex;
            ProtocolTrace.Write("H17", "ClientWebSocketTransport.ReceiveLoopAsync", "receive_error", new
            {
                type = ex.GetType().Name,
                message = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message
            });
        }
        finally
        {
            Interlocked.Exchange(ref _connected, 0);
            ProtocolTrace.Write("H17", "ClientWebSocketTransport.ReceiveLoopAsync", "receive_loop_end", new
            {
                frames = Volatile.Read(ref _frameCount),
                closeStatus = closeStatus?.ToString(),
                err = error?.GetType().Name
            });
            Closed?.Invoke(error);
        }
    }

    private static void ApplyCookies(ClientWebSocket socket, Uri uri, string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return;

        try
        {
            var container = new System.Net.CookieContainer();
            var added = 0;
            foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim();
                if (name.Length == 0) continue;
                // CookieContainer — NOT raw Cookie request header (raw header blackholed Engine.IO).
                container.Add(new Uri($"{uri.Scheme}://{uri.Host}/"), new System.Net.Cookie(name, value));
                added++;
            }

            if (added == 0)
                return;

            socket.Options.Cookies = container;
            // #region agent log
            ProtocolTrace.Write("H41", "ClientWebSocketTransport.ApplyCookies", "cookie_container_applied", new
            {
                added,
                host = uri.Host
            });
            // #endregion
        }
        catch (Exception ex)
        {
            ProtocolTrace.Write("H41", "ClientWebSocketTransport.ApplyCookies", "cookie_container_failed", new
            {
                type = ex.GetType().Name
            });
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
