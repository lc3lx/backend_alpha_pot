using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Contracts;

public sealed record TelegramAuthRequest(string InitData);

public sealed record AuthSessionResponse(string AccessToken, string UserId);

public sealed record MeResponse(
    string UserId,
    long TelegramUserId,
    string? Username,
    string? FullName,
    string? Country,
    string Role,
    bool IsAdmin,
    BinollaStatusDto? Binolla);

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

public sealed record StrategyDto(
    string Id,
    string Name,
    string Status,
    bool Enabled);

public sealed record StrategiesResponse(IReadOnlyList<StrategyDto> Strategies);

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
    long TelegramUserId,
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
    int Total);

public sealed record ApiErrorResponse(string Code, string Message);
