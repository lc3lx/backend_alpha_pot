using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Domain.Entities;

public class User
{
    public Guid Id { get; set; }
    public long TelegramUserId { get; set; }
    public string? Username { get; set; }
    public string? FullName { get; set; }
    public string? Country { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public BinollaLink? BinollaLink { get; set; }
    public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
    public ICollection<Trade> Trades { get; set; } = new List<Trade>();
}

public class BinollaLink
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string EncryptedSsid { get; set; } = string.Empty;
    public string? BinollaAccountIdentifier { get; set; }
    public BinollaAccountType AccountType { get; set; } = BinollaAccountType.Demo;
    public BinollaLinkStatus Status { get; set; } = BinollaLinkStatus.Disconnected;

    /// <summary>Legacy — not an access gate after Phase 7.</summary>
    public ReferralStatus ReferralStatus { get; set; } = ReferralStatus.Unknown;
    public DateTimeOffset? ReferralCheckedAt { get; set; }

    /// <summary>Manual admin approval — source of truth for free bot access.</summary>
    public bool AdminApproved { get; set; }
    public AdminApprovalStatus ApprovalStatus { get; set; } = AdminApprovalStatus.Pending;
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}

public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ActivationKey { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Inactive;
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
}

public class Trade
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? BinollaOrderId { get; set; }
    public string Asset { get; set; } = string.Empty;
    public TradeDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public int DurationSeconds { get; set; }
    public TradeStatus Status { get; set; } = TradeStatus.Pending;
    public decimal? Pnl { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}

/// <summary>Security-sensitive admin action audit (no secrets).</summary>
public class AuditEvent
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid ActorUserId { get; set; }
    public Guid? TargetUserId { get; set; }
    public Guid? TargetBinollaLinkId { get; set; }
    public string? PreviousState { get; set; }
    public string? NewState { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
