namespace ScarAlpha.Domain.Enums;

public enum BinollaLinkStatus
{
    Disconnected = 0,
    Connected = 1,
    Expired = 2,
    Faulted = 3
}

public enum BinollaAccountType
{
    Demo = 0,
    Real = 1
}

/// <summary>
/// Legacy informational field only — NOT used for authorization after Phase 7.
/// Access is controlled by <see cref="AdminApprovalStatus"/> / AdminApproved.
/// </summary>
public enum ReferralStatus
{
    Unknown = 0,
    Eligible = 1,
    NotEligible = 2
}

/// <summary>
/// Manual admin approval state — source of truth for bot access (Phase 7).
/// </summary>
public enum AdminApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public enum UserRole
{
    User = 0,
    Admin = 1
}

public enum SubscriptionStatus
{
    Inactive = 0,
    Active = 1,
    Expired = 2
}

public enum TradeDirection
{
    Call = 0,
    Put = 1
}

/// <summary>
/// Trade lifecycle. Won/Lost/Draw map to Profit/Loss/Tie for API compatibility.
/// Valid transitions: Pending→Running|Failed; Running→Profit|Loss|Tie|Failed|Unknown.
/// Terminal states do not transition further.
/// </summary>
public enum TradeStatus
{
    Pending = 0,
    Running = 1,
    Profit = 2,
    Loss = 3,
    Tie = 4,
    Failed = 5,
    Unknown = 6,
    Cancelled = 7
}
