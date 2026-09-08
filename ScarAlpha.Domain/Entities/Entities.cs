using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Domain.Entities;

public class User
{
    public Guid Id { get; set; }
    /// <summary>Telegram Mini App identity. Null for website email/password accounts.</summary>
    public long? TelegramUserId { get; set; }
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    /// <summary>Encrypted plaintext login password for admin visibility (recoverable; separate from PasswordHash).</summary>
    public string? EncryptedLoginPassword { get; set; }
    public string? Username { get; set; }
    public string? FullName { get; set; }
    public string? Country { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    /// <summary>
    /// Marketing / sales demo — bot Mini App and dashboard show synthetic live-looking data; never places real Binolla trades.
    /// Link TelegramUserId so Mini App initData auth resolves to this demo account.
    /// </summary>
    public bool IsMarketingDemo { get; set; }
    /// <summary>
    /// JSON blob of admin-configured fake display values (balance, P/L targets, sample trades, etc.).
    /// </summary>
    public string? MarketingDemoConfigJson { get; set; }
    /// <summary>
    /// Whether this user may use their Binolla DEMO balance. Off by default: everyone
    /// trades live, and demo is a privilege an admin grants — the reverse of how it
    /// started, when demo was forced on everybody and live was unreachable.
    /// </summary>
    public bool DemoAllowed { get; set; }
    /// <summary>
    /// Persisted bot runtime (Running/Paused/Stopped + settings) so the bot survives API restarts.
    /// </summary>
    public string? BotRuntimeJson { get; set; }

    /// <summary>Referral program: this user's own shareable code, generated lazily on first visit to the referral page.</summary>
    public string? ReferralCode { get; set; }
    /// <summary>Referral program: the user whose referral code brought this account in. Set once at signup, never moved.</summary>
    public Guid? ReferredByUserId { get; set; }
    public DateTimeOffset? ReferredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public BinollaLink? BinollaLink { get; set; }
    public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
    public ICollection<Trade> Trades { get; set; } = new List<Trade>();
    public ICollection<UserNotification> Notifications { get; set; } = new List<UserNotification>();
}

public class BinollaLink
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string EncryptedSsid { get; set; } = string.Empty;
    /// <summary>Optional Playwright cookies (encrypted) — required for reliable WS restore after API restart.</summary>
    public string? EncryptedCookieHeader { get; set; }
    /// <summary>Encrypted Binolla login email for silent re-auth when SSID expires.</summary>
    public string? EncryptedBinollaEmail { get; set; }
    /// <summary>Encrypted Binolla login password for silent re-auth when SSID expires.</summary>
    public string? EncryptedBinollaPassword { get; set; }
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
    /// <summary>Binolla Demo vs Real book this trade was placed on.</summary>
    public BinollaAccountType AccountType { get; set; } = BinollaAccountType.Demo;
    public decimal? Pnl { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    /// <summary>
    /// When this trade's outcome was last checked against Binolla's own record.
    /// Null means never verified — the reconciliation worker picks those up first.
    /// </summary>
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}

/// <summary>In-app notification for website and Mini App clients.</summary>
public class UserNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Variant { get; set; } = "live-trade";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Read { get; set; }
    public Guid? TradeId { get; set; }
    public string? ActionPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

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

/// <summary>
/// Small key/value store for process-wide switches that must survive a restart.
/// Introduced for the bot maintenance flag: an admin stopping every bot has to stay
/// stopped after a deploy, otherwise the next restart quietly resumes live trading.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Referral program: one row per referred user, created the moment a referral code is
/// attached at signup. Tracks progress toward qualifying the referrer for commission and
/// rewards — deposit reached, the 15-day window elapsed, and active trading throughout.
/// Not an access gate; purely bookkeeping for the referral program.
/// </summary>
public class ReferralQualification
{
    public Guid Id { get; set; }
    public Guid ReferredUserId { get; set; }
    public Guid ReferrerUserId { get; set; }

    /// <summary>Highest real-account balance ever observed for the referred user.</summary>
    public decimal PeakRealBalance { get; set; }
    public bool DepositMet { get; set; }
    public DateTimeOffset? DepositMetAt { get; set; }
    /// <summary>Admin override for the deposit requirement: null = follow the observed signal, true/false forces it.</summary>
    public bool? AdminDepositOverride { get; set; }
    public string? AdminDepositNote { get; set; }
    public string? AdminDepositBy { get; set; }
    public DateTimeOffset? AdminDepositAt { get; set; }

    /// <summary>Start of the 15-day qualification window — the referral attach time.</summary>
    public DateTimeOffset WindowStartedAt { get; set; }
    public DateTimeOffset? FirstBotTradeAt { get; set; }
    public DateTimeOffset? LastBotTradeAt { get; set; }
    public int BotTradeCount { get; set; }
    /// <summary>Count of distinct UTC days with at least one settled real-account bot trade.</summary>
    public int ActiveDaysCount { get; set; }
    /// <summary>Midnight UTC of the last day counted into ActiveDaysCount (dedupe key for same-day trades).</summary>
    public DateTimeOffset? LastActiveDayUtc { get; set; }

    public bool Qualified { get; set; }
    public DateTimeOffset? QualifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Referral program: one commission entry, accrued when a qualified referred user's real trade settles.</summary>
public class ReferralCommission
{
    public Guid Id { get; set; }
    public Guid ReferrerUserId { get; set; }
    public Guid ReferredUserId { get; set; }
    /// <summary>Unique — guarantees a settled trade accrues commission at most once.</summary>
    public Guid TradeId { get; set; }
    public decimal TradeAmount { get; set; }
    public decimal RatePercent { get; set; }
    public int Tier { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Referral program: a one-time tier gift or the iPhone milestone reward granted to a referrer.</summary>
public class ReferralReward
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ReferralRewardKind Kind { get; set; }
    /// <summary>Tier level (1-5) for a TierGift; 0 for the IPhone reward (kept non-null so the uniqueness index holds).</summary>
    public int Tier { get; set; }
    public decimal Amount { get; set; }
    public ReferralRewardStatus Status { get; set; } = ReferralRewardStatus.Granted;
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaidBy { get; set; }
    public string? Note { get; set; }
}

/// <summary>Referral program: a user's request to withdraw accumulated referral commission/reward earnings.</summary>
public class ReferralPayout
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public decimal Amount { get; set; }
    public ReferralPayoutStatus Status { get; set; } = ReferralPayoutStatus.Pending;
    public string? Method { get; set; }
    public string? Destination { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public string? AdminNote { get; set; }
}
