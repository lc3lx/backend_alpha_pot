namespace ScarAlpha.Binolla.Models;

public enum AccountType
{
    Real = 0,
    Demo = 1
}

public enum TradeDirection
{
    Call = 0,
    Put = 1
}

public enum OrderStatus
{
    Pending = 0,
    Open = 1,
    Closed = 2,
    Cancelled = 3,
    Failed = 4
}

public enum TradeResult
{
    Pending = 0,
    Win = 1,
    Loss = 2,
    Tie = 3
}

public enum TradeType
{
    Blitz = 0,
    FixedTime = 1
}

public enum SessionLifecycleState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Reconnected = 4,
    AuthenticationFailed = 5,
    SessionExpired = 6,
    Faulted = 7
}
