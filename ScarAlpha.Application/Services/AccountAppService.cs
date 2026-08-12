using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;

namespace ScarAlpha.Application.Services;

public sealed class AccountAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBotAccessService _access;

    public AccountAppService(ICurrentUser currentUser, IBotAccessService access)
    {
        _currentUser = currentUser;
        _access = access;
    }

    public async Task<AccountStatusResponse> GetStatusAsync(CancellationToken ct)
    {
        var result = await _access.CheckAsync(_currentUser.UserId, ct);
        return new AccountStatusResponse(
            BinollaConnected: result.BinollaConnected,
            AccountType: result.AccountType,
            AdminApproved: result.AdminApproved,
            ApprovalStatus: result.ApprovalStatus,
            BotAccess: MapAccess(result.Access));
    }

    internal static string MapAccess(BotAccessState access) => access switch
    {
        BotAccessState.Allowed => "Allowed",
        BotAccessState.BinollaNotConnected => "BinollaNotConnected",
        BotAccessState.AdminApprovalRequired => "AdminApprovalRequired",
        BotAccessState.NotEligible => "NotEligible",
        BotAccessState.SessionExpired => "SessionExpired",
        _ => "BinollaNotConnected"
    };

    /// <summary>
    /// Full bot access including Demo trading — requires admin approval.
    /// </summary>
    internal static void EnsureAllowed(BotAccessResult access)
    {
        switch (access.Access)
        {
            case BotAccessState.Allowed:
                return;
            case BotAccessState.SessionExpired:
                throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
            case BotAccessState.NotEligible:
                throw new ApiException(ApiErrorCodes.NotEligible, "Account was rejected by an administrator.", 403);
            case BotAccessState.AdminApprovalRequired:
                throw new ApiException(ApiErrorCodes.AdminApprovalRequired,
                    "Administrator has not approved your account yet. Trading is locked until approval.", 403);
            default:
                throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Connect your Binolla account first.", 409);
        }
    }

    /// <summary>
    /// Market / RSI / balance: connected Binolla is enough; pending admin approval is allowed.
    /// Trading still uses <see cref="EnsureAllowed"/>.
    /// </summary>
    internal static void EnsureConnectedForMarket(BotAccessResult access)
    {
        switch (access.Access)
        {
            case BotAccessState.Allowed:
            case BotAccessState.AdminApprovalRequired:
                return;
            case BotAccessState.SessionExpired:
                throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
            case BotAccessState.NotEligible:
                throw new ApiException(ApiErrorCodes.NotEligible, "Account was rejected by an administrator.", 403);
            default:
                throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Connect your Binolla account first.", 409);
        }
    }
}
