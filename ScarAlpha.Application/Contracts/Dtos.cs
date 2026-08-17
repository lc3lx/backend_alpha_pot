using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Contracts;

public sealed record TelegramAuthRequest(string InitData);

public sealed record EmailAuthRequest(
    string Email,
    string Password,
    string? FullName = null,
    string? Country = null,
    string? Username = null);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UpdateProfileRequest(string? FullName, string? Country, string? Username);

public sealed record AuthSessionResponse(string AccessToken, string UserId);

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

public sealed record BinollaConnectRequest(string Ssid, string AccountType = "Demo");

public sealed record BinollaCredentialRequest(string Email, string Password, string AccountType = "Demo");

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
    IReadOnlyList<string>? Assets = null);
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
    IReadOnlyList<string>? Assets = null);
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
    string? StopReason = null);

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
    DateTimeOffset UpdatedAt);

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
    string? ApprovedBy);

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
    decimal WinRatePercent = 62m,
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
    string? BinollaApprovalStatus,
    bool BinollaConnected,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

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
    MarketingDemoConfigDto? MarketingConfig,
    AdminBinollaAccountDto? BinollaAccount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PatchAdminUserRequest(
    bool? IsMarketingDemo = null,
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
    IReadOnlyList<string> Assets);

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
    IReadOnlyList<string>? Assets = null);

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
