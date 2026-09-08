using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Contracts;

public sealed record TelegramAuthRequest(string InitData, string? ReferralCode = null);

public sealed record EmailAuthRequest(
    string Email,
    string Password,
    string? FullName = null,
    string? Country = null,
    string? Username = null,
    string? ReferralCode = null);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UpdateProfileRequest(string? FullName, string? Country, string? Username);

public sealed record AuthSessionResponse(string AccessToken, string UserId);

/// <summary>Public Binolla login/signup — issues JWT and connects Binolla in one step.</summary>
public sealed record BinollaAuthResponse(
    string AccessToken,
    string UserId,
    bool Connected,
    string AccountType,
    string Access,
    bool AdminApproved,
    string ApprovalStatus,
    DateTimeOffset? LastConnectedAt,
    decimal? Balance);

public sealed record MeResponse(
    string UserId,
    long? TelegramUserId,
    string? Email,
    bool HasPassword,
    string? Username,
    string? FullName,
    string? Country,
    string Role,
    bool IsAdmin,
    BinollaStatusDto? Binolla,
    bool IsMarketingDemo = false);

// Default is Real: live is the product's normal state and demo is an admin-granted
// privilege. Leaving these at "Demo" meant a caller that simply omitted the field was
// asking for the locked balance and got refused.
public sealed record BinollaConnectRequest(string Ssid, string AccountType = "Real");

public sealed record BinollaCredentialRequest(
    string Email,
    string Password,
    string AccountType = "Real",
    string? ReferralCode = null);

public sealed record BinollaAccountTypeRequest(string AccountType);

public sealed record BinollaConnectResponse(
    bool Connected,
    string AccountType,
    string Access,
    bool AdminApproved,
    string ApprovalStatus,
    DateTimeOffset? LastConnectedAt,
    decimal? Balance);

public sealed record AccountStatusResponse(
    bool BinollaConnected,
    string AccountType,
    bool AdminApproved,
    string ApprovalStatus,
    string BotAccess);

public sealed record AccountSubscriptionResponse(
    string PlanName,
    string Status,
    string StatusLabel,
    string ApprovalStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ApprovedAt,
    string KeyUsedLabel);

public sealed record ActivationHistoryItemDto(
    string Id,
    string KeyLabel,
    string Status,
    string StatusLabel,
    string PreviousState,
    string NewState,
    DateTimeOffset CreatedAt);

public sealed record ActivationHistoryResponse(IReadOnlyList<ActivationHistoryItemDto> Items);

public sealed record NotificationDto(
    string Id,
    string Variant,
    string Title,
    string Description,
    bool Read,
    string? TradeId,
    string? ActionPath,
    DateTimeOffset CreatedAt);

public sealed record NotificationListResponse(IReadOnlyList<NotificationDto> Items, int UnreadCount);

public sealed record StrategyDto(
    string Id,
    string Name,
    string Status,
    bool Enabled);

public sealed record StrategiesResponse(IReadOnlyList<StrategyDto> Strategies);

public sealed record BotStartRequest(
    string? Asset = null,
    decimal Amount = 25m,
    int DurationSeconds = 300,
    decimal DailyProfitTarget = 50m,
    decimal DailyLossLimit = 30m,
    bool AutoStopAtProfit = true,
    bool AutoStopAtLoss = true,
    bool SignalConfirmationEnabled = true,
    string RiskLevel = "risk-medium",
    bool NotificationsEnabled = true,
    IReadOnlyList<string>? Assets = null,
    /// <summary>Strategy to run: "rsi" (default) or "ema".</summary>
    string StrategyId = "rsi",
    /// <summary>Stake progression mode (technical indicator).</summary>
    string StakeMode = "red-signal-pro",
    string? MarketTypeId = null);
public sealed record BotApplyRequest(
    string? Asset = null,
    decimal? Amount = null,
    int? DurationSeconds = null,
    decimal? DailyProfitTarget = null,
    decimal? DailyLossLimit = null,
    bool? AutoStopAtProfit = null,
    bool? AutoStopAtLoss = null,
    bool? SignalConfirmationEnabled = null,
    string? RiskLevel = null,
    bool? NotificationsEnabled = null,
    IReadOnlyList<string>? Assets = null,
    string? StrategyId = null,
    string? StakeMode = null,
    string? MarketTypeId = null);
public sealed record BotRuntimeDto(
    string State,
    string? Asset,
    decimal Amount,
    int DurationSeconds,
    decimal DailyProfitTarget,
    decimal DailyLossLimit,
    DateTimeOffset UpdatedAt,
    bool AutoStopAtProfit,
    bool AutoStopAtLoss,
    bool SignalConfirmationEnabled,
    string RiskLevel,
    bool NotificationsEnabled,
    IReadOnlyList<string> Assets,
    DateTimeOffset? PnlSessionStartedAt = null,
    string? StopReason = null,
    string StrategyId = "rsi",
    decimal BaseAmount = 0m,
    string StakeMode = "red-signal-pro",
    string MarketTypeId = "all-markets",
    /// <summary>
    /// Set while an admin has the whole fleet stopped. The bot page shows the maintenance
    /// notice instead of its controls while this is present.
    /// </summary>
    BotMaintenanceDto? Maintenance = null);

public sealed record BinollaStatusDto(
    bool Connected,
    string AccountType,
    string Status,
    DateTimeOffset? LastConnectedAt,
    decimal? Balance,
    string? Lifecycle = null,
    bool WebSocketConnected = false);

public sealed record BinollaBalanceDto(
    bool Connected,
    string AccountType,
    decimal DemoBalance,
    decimal RealBalance,
    decimal CurrentBalance);

public sealed record PlaceTradeRequest(
    string Asset,
    string Direction,
    decimal Amount,
    int DurationSeconds,
    string StrategyId = "rsi");

public sealed record TradeDto(
    string Id,
    string? BinollaOrderId,
    string Asset,
    string Direction,
    decimal Amount,
    int DurationSeconds,
    string Status,
    decimal? Pnl,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string AccountType = "Demo");

// ---- Referral program ----

public sealed record ReferralTierDto(
    int Level,
    int MinReferrals,
    decimal RatePercent,
    decimal GiftUsd,
    bool Reached,
    bool IsCurrent);

public sealed record ReferralSummaryResponse(
    string ReferralCode,
    string ReferralLink,
    string TelegramShareLink,
    int QualifiedReferrals,
    int TotalReferrals,
    int PendingReferrals,
    int CurrentTier,
    decimal CurrentRatePercent,
    ReferralTierDto? NextTier,
    int ReferralsToNextTier,
    decimal TotalCommissionEarned,
    decimal TotalRewardsEarned,
    decimal TotalPaidOut,
    decimal AvailableBalance,
    decimal MinPayoutUsd,
    int IPhoneQualifiedReferrals,
    int IPhoneTarget,
    bool IPhoneEligible,
    IReadOnlyList<ReferralTierDto> Tiers);

public sealed record ReferralMemberDto(
    string UserId,
    string? DisplayName,
    DateTimeOffset ReferredAt,
    bool DepositMet,
    int ActiveDaysCount,
    int RequiredActiveDays,
    int QualificationDays,
    DateTimeOffset WindowEndsAt,
    bool Qualified,
    DateTimeOffset? QualifiedAt,
    /// <summary>True once this person has actually started trading.</summary>
    bool IsActive = false,
    /// <summary>Settled bot trades recorded for this person.</summary>
    int TradeCount = 0,
    /// <summary>Total stake across the trades that paid the referrer.</summary>
    decimal TradedVolume = 0m,
    /// <summary>What the referrer has earned from this person so far.</summary>
    decimal CommissionEarned = 0m,
    /// <summary>The commission rate currently applied, as a percent of stake.</summary>
    decimal RatePercent = 0m);

public sealed record ReferralMembersResponse(IReadOnlyList<ReferralMemberDto> Items, int Total, int Page, int PageSize);

public sealed record ReferralCommissionDto(
    string Id,
    string ReferredUserId,
    string? ReferredDisplayName,
    decimal TradeAmount,
    decimal RatePercent,
    int Tier,
    decimal Amount,
    DateTimeOffset CreatedAt);

public sealed record ReferralCommissionsResponse(IReadOnlyList<ReferralCommissionDto> Items, int Total, int Page, int PageSize);

public sealed record ReferralRewardDto(
    string Id,
    string Kind,
    int? Tier,
    decimal Amount,
    string Status,
    DateTimeOffset GrantedAt,
    DateTimeOffset? PaidAt,
    string? Note);

public sealed record ReferralRewardsResponse(IReadOnlyList<ReferralRewardDto> Items);

public sealed record ReferralPayoutRequest(decimal Amount, string Method, string Destination);

public sealed record ReferralPayoutDto(
    string Id,
    decimal Amount,
    string Status,
    string? Method,
    string? Destination,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    string? AdminNote);

public sealed record ReferralPayoutsResponse(IReadOnlyList<ReferralPayoutDto> Items, int Total, int Page, int PageSize);

public sealed record AdminReferralOverviewDto(
    string ReferrerUserId,
    string? DisplayName,
    string? Email,
    int QualifiedReferrals,
    int TotalReferrals,
    int CurrentTier,
    decimal TotalCommissionEarned,
    decimal TotalRewardsEarned,
    decimal AvailableBalance);

public sealed record AdminReferralOverviewListResponse(IReadOnlyList<AdminReferralOverviewDto> Items, int Total, int Page, int PageSize);

public sealed record AdminReferralDetailResponse(
    AdminReferralOverviewDto Referrer,
    IReadOnlyList<ReferralMemberDto> Members,
    IReadOnlyList<ReferralCommissionDto> RecentCommissions,
    IReadOnlyList<ReferralRewardDto> Rewards);

public sealed record AdminDepositOverrideRequest(bool? Met, string? Note);

/// <summary>Decision: "approve" | "reject" | "paid".</summary>
public sealed record AdminPayoutDecisionRequest(string Decision, string? Note);

public sealed record AdminReferralPayoutDto(
    string Id,
    string UserId,
    string? UserDisplayName,
    decimal Amount,
    string Status,
    string? Method,
    string? Destination,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    string? AdminNote);

public sealed record AdminReferralPayoutsResponse(IReadOnlyList<AdminReferralPayoutDto> Items, int Total, int Page, int PageSize);

public sealed record AdminRewardPaidRequest(string? Note);

public sealed record TradeListResponse(
    IReadOnlyList<TradeDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record MarketAssetDto(
    string Symbol,
    string Name,
    bool Available,
    int? Payout);

public sealed record MarketAssetsResponse(IReadOnlyList<MarketAssetDto> Assets);

public sealed record MarketPriceResponse(
    string Asset,
    decimal? Price,
    DateTimeOffset Timestamp);

public sealed record MarketCandleDto(
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close);

public sealed record MarketCandlesResponse(
    string Asset,
    int Period,
    IReadOnlyList<MarketCandleDto> Candles);

public sealed record AdminBinollaAccountDto(
    string Id,
    string UserId,
    long? TelegramUserId,
    string? Email,
    string? Username,
    string? FullName,
    string? BinollaAccountIdentifier,
    string ConnectionStatus,
    string ApprovalStatus,
    bool AdminApproved,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    string? ApprovedBy,
    /// <summary>Decrypted Binolla / login email for admin panel.</summary>
    string? LoginEmail = null,
    /// <summary>Decrypted Binolla / login password for admin panel.</summary>
    string? LoginPassword = null);

public sealed record AdminBinollaAccountListResponse(
    IReadOnlyList<AdminBinollaAccountDto> Items,
    int Total,
    int Page = 1,
    int PageSize = 50);

public sealed record MarketingDemoTradeSeedDto(
    string Asset,
    string Direction,
    decimal Amount,
    string Status,
    decimal? Pnl = null,
    int DurationSeconds = 60,
    int MinutesAgo = 0);

/// <summary>
/// Admin-configured fake figures for a marketing demo account. Drives bot UI numbers.
/// </summary>
public sealed record MarketingDemoConfigDto(
    decimal Balance = 12_450m,
    decimal BalanceWobble = 28m,
    decimal TotalProfit = 3_200m,
    decimal TotalLoss = 1_100m,
    decimal WinRatePercent = 76m,
    int HistoryTradeCount = 40,
    decimal DefaultTradeAmount = 25m,
    bool IncludeRunningTrade = true,
    string? PlanName = null,
    IReadOnlyList<MarketingDemoTradeSeedDto>? SampleTrades = null);

public sealed record CreateMarketingDemoUserRequest(
    string? Email = null,
    string? Password = null,
    string? FullName = null,
    string? Username = null,
    long? TelegramUserId = null,
    MarketingDemoConfigDto? Config = null);

public sealed record SetMarketingDemoRequest(
    bool IsMarketingDemo,
    long? TelegramUserId = null,
    MarketingDemoConfigDto? Config = null);

public sealed record UpdateMarketingDemoConfigRequest(MarketingDemoConfigDto Config);

public sealed record MarketingDemoUserDto(
    string Id,
    string? Email,
    string? FullName,
    string? Username,
    long? TelegramUserId,
    bool IsMarketingDemo,
    DateTimeOffset CreatedAt,
    MarketingDemoConfigDto Config);

public sealed record MarketingDemoUserListResponse(
    IReadOnlyList<MarketingDemoUserDto> Items,
    int Total,
    int Page = 1,
    int PageSize = 50);

public sealed record AdminUserListItemDto(
    string Id,
    string? Email,
    string? FullName,
    string? Username,
    long? TelegramUserId,
    string Role,
    bool IsMarketingDemo,
    /// <summary>Whether an admin has unlocked the Binolla demo balance for this user.</summary>
    bool DemoAllowed,
    string? BinollaApprovalStatus,
    bool BinollaConnected,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LoginEmail = null,
    string? LoginPassword = null);

public sealed record AdminUserListResponse(
    IReadOnlyList<AdminUserListItemDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record AdminUserDetailDto(
    string Id,
    string? Email,
    string? FullName,
    string? Username,
    string? Country,
    long? TelegramUserId,
    string Role,
    bool IsAdmin,
    bool IsMarketingDemo,
    /// <summary>Whether an admin has unlocked the Binolla demo balance for this user.</summary>
    bool DemoAllowed,
    MarketingDemoConfigDto? MarketingConfig,
    AdminBinollaAccountDto? BinollaAccount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LoginEmail = null,
    string? LoginPassword = null);

public sealed record PatchAdminUserRequest(
    bool? IsMarketingDemo = null,
    /// <summary>Unlock/lock the Binolla demo balance. Null leaves it unchanged.</summary>
    bool? DemoAllowed = null,
    long? TelegramUserId = null,
    bool ClearTelegramUserId = false,
    MarketingDemoConfigDto? Config = null);

public sealed record AdminAuditEventDto(
    string Id,
    string Action,
    string ActorUserId,
    string? TargetUserId,
    string? TargetBinollaLinkId,
    string? PreviousState,
    string? NewState,
    string? Detail,
    DateTimeOffset CreatedAt);

public sealed record AdminAuditListResponse(
    IReadOnlyList<AdminAuditEventDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record AdminSendNotificationRequest(
    string Title,
    string Description,
    IReadOnlyList<string>? UserIds = null,
    bool AllApprovedUsers = false,
    string Variant = "admin-message",
    string? ActionPath = null);

public sealed record AdminSendNotificationResponse(
    int Sent,
    IReadOnlyList<string> UserIds);

public sealed record AdminNotificationDto(
    string Id,
    string UserId,
    string Variant,
    string Title,
    string Description,
    bool Read,
    string? ActionPath,
    DateTimeOffset CreatedAt);

public sealed record AdminNotificationListResponse(
    IReadOnlyList<AdminNotificationDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record AdminBotRuntimeDto(
    string UserId,
    string? Email,
    string? FullName,
    long? TelegramUserId,
    string BotAccess,
    string State,
    string? Asset,
    decimal Amount,
    int DurationSeconds,
    decimal DailyProfitTarget,
    decimal DailyLossLimit,
    DateTimeOffset UpdatedAt,
    bool IsMarketingDemo,
    IReadOnlyList<string> Assets,
    /// <summary>Strategy this bot runs, so an admin can see and change it.</summary>
    string StrategyId = "rsi");

public sealed record AdminBotListResponse(
    IReadOnlyList<AdminBotRuntimeDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record AdminBotControlRequest(
    string Action,
    string? Asset = null,
    decimal? Amount = null,
    int? DurationSeconds = null,
    decimal? DailyProfitTarget = null,
    decimal? DailyLossLimit = null,
    IReadOnlyList<string>? Assets = null,
    /// <summary>Strategy the bot should run (rsi, ema, alt5, smart…). Null keeps the current one.</summary>
    string? StrategyId = null);

/// <summary>Turns the global bot stop on or off.</summary>
public sealed record AdminMaintenanceRequest(bool Active, string? Message = null);

/// <summary>Result of a fleet-wide start/stop.</summary>
public sealed record AdminFleetActionResponse(
    bool MaintenanceActive,
    string? Message,
    DateTimeOffset? Since,
    int BotsAffected);

/// <summary>Maintenance state as the user's app sees it.</summary>
public sealed record BotMaintenanceDto(bool Active, string? Message, DateTimeOffset? Since);

public sealed record AdminTradeDto(
    string Id,
    string UserId,
    string? Email,
    string? FullName,
    string Asset,
    string Direction,
    decimal Amount,
    string Status,
    decimal? Pnl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt);

public sealed record AdminTradeListResponse(
    IReadOnlyList<AdminTradeDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record ApiErrorResponse(string Code, string Message);
