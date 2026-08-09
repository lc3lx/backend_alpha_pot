namespace ScarAlpha.Binolla.Transport;

/// <summary>
/// Thin transport abstraction so session logic can be tested without a real network.
/// Real implementation wraps ClientWebSocket with upstream headers.
/// </summary>
public interface IWebSocketTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    event Action<string>? TextMessageReceived;
    event Action<Exception?>? Closed;

    Task ConnectAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken);

    Task SendAsync(string message, CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

public delegate IWebSocketTransport WebSocketTransportFactory();
