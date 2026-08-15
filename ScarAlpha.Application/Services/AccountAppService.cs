using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;

namespace ScarAlpha.Application.Services;

public sealed class AccountAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBotAccessService _access;
    private readonly IAuditService _audit;
    private readonly IBinollaLinkRepository _links;
    private readonly IUserRepository _users;
    private readonly IMarketingDemoService _demo;

    public AccountAppService(
        ICurrentUser currentUser,
        IBotAccessService access,
        IAuditService audit,
        IBinollaLinkRepository links,
        IUserRepository users,
        IMarketingDemoService demo)
    {
        _currentUser = currentUser;
        _access = access;
        _audit = audit;
        _links = links;
        _users = users;
        _demo = demo;
    }

    public async Task<AccountStatusResponse> GetStatusAsync(CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.BuildAccountStatus();

        var result = await _access.CheckAsync(_currentUser.UserId, ct);
        return new AccountStatusResponse(
            BinollaConnected: result.BinollaConnected,
            AccountType: result.AccountType,
            AdminApproved: result.AdminApproved,
            ApprovalStatus: result.ApprovalStatus,
            BotAccess: MapAccess(result.Access));
    }

    public async Task<AccountSubscriptionResponse> GetSubscriptionAsync(CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
        {
            var demoUser = await _users.GetByIdAsync(_currentUser.UserId, ct);
            return _demo.BuildSubscription(_currentUser.UserId, demoUser?.CreatedAt);
        }

        var result = await _access.CheckAsync(_currentUser.UserId, ct);
        var link = await _links.GetByUserIdAsync(_currentUser.UserId, ct);
        return new AccountSubscriptionResponse(
            PlanName: "Free (admin approved)",
            Status: result.Access == BotAccessState.Allowed ? "active" : "pending",
            StatusLabel: MapAccess(result.Access),
            ApprovalStatus: result.ApprovalStatus,
            StartedAt: link?.CreatedAt,
            ApprovedAt: link?.ApprovedAt,
            KeyUsedLabel: $"Approval: {result.ApprovalStatus}");
    }

    public async Task<ActivationHistoryResponse> GetActivationHistoryAsync(CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.BuildActivationHistory(_currentUser.UserId);

        var events = await _audit.ListForTargetUserAsync(_currentUser.UserId, 50, ct);
        var items = events.Select(e =>
        {
            var approved = string.Equals(e.Action, "BinollaAccountApproved", StringComparison.Ordinal);
            return new ActivationHistoryItemDto(
                Id: e.Id.ToString(),
                KeyLabel: HumanizeAction(e.Action),
                Status: approved ? "active" : "expired",
                StatusLabel: e.NewState ?? e.Action,
                PreviousState: e.PreviousState ?? "—",
                NewState: e.NewState ?? "—",
                CreatedAt: e.CreatedAt);
        }).ToList();

        return new ActivationHistoryResponse(items);
    }

    private static string HumanizeAction(string action) => action switch
    {
        "BinollaAccountApproved" => "Admin approved",
        "BinollaAccountRejected" => "Admin rejected",
        _ => action
    };

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
