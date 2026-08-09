namespace ScarAlpha.Binolla.Models;

/// <summary>
/// Engine exceptions. Messages must never include SSID/token material.
/// </summary>
public class BinollaException : Exception
{
    public BinollaException(string message) : base(message) { }
    public BinollaException(string message, Exception inner) : base(message, inner) { }
}

public sealed class BinollaAuthenticationException : BinollaException
{
    public BinollaAuthenticationException(string message) : base(message) { }
}

public sealed class BinollaConnectionException : BinollaException
{
    public BinollaConnectionException(string message) : base(message) { }
}

public sealed class BinollaTimeoutException : BinollaException
{
    public BinollaTimeoutException(string message) : base(message) { }
}

public sealed class BinollaOrderException : BinollaException
{
    public BinollaOrderException(string message) : base(message) { }
}
