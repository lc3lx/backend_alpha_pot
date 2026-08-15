namespace ScarAlpha.Application.Common;

public static class ApiErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string TelegramAuthInvalid = "TELEGRAM_AUTH_INVALID";
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string TelegramTaken = "TELEGRAM_TAKEN";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string PasswordNotSet = "PASSWORD_NOT_SET";
    public const string BinollaNotConnected = "BINOLLA_NOT_CONNECTED";
    public const string BinollaSessionExpired = "BINOLLA_SESSION_EXPIRED";
    public const string BinollaConnectionFailed = "BINOLLA_CONNECTION_FAILED";
    public const string BinollaLoginFailed = "BINOLLA_LOGIN_FAILED";
    public const string BinollaMarketUnavailable = "BINOLLA_MARKET_UNAVAILABLE";
    public const string MarketUnavailable = "MARKET_UNAVAILABLE";
    public const string InsufficientBalance = "INSUFFICIENT_BALANCE";
    public const string InvalidTrade = "INVALID_TRADE";
    public const string RealTradingDisabled = "REAL_TRADING_DISABLED";
    public const string DuplicateRequest = "DUPLICATE_REQUEST";
    public const string NotFound = "NOT_FOUND";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string RateLimited = "RATE_LIMITED";
    public const string AdminApprovalRequired = "ADMIN_APPROVAL_REQUIRED";
    public const string NotEligible = "NOT_ELIGIBLE";
    public const string Forbidden = "FORBIDDEN";
    public const string BotAccessDenied = "BOT_ACCESS_DENIED";
    public const string StrategyDisabled = "STRATEGY_DISABLED";
    public const string StrategyNotFound = "STRATEGY_NOT_FOUND";
}

public sealed class ApiException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }

    public ApiException(string code, string message, int statusCode = 400) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}
