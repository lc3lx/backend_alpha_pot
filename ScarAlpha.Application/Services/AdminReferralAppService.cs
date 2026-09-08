using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

/// <summary>Admin side of the referral program: overview, deposit overrides, payout decisions, reward payouts.</summary>
public sealed class AdminReferralAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IReferralRepository _referrals;
    private readonly IUserRepository _users;
    private readonly IReferralQualificationService _qualification;
    private readonly IAuditService _audit;
    private readonly INotificationWriter _notifications;
    private readonly ILogger<AdminReferralAppService> _logger;

    public AdminReferralAppService(
        ICurrentUser currentUser,
        IReferralRepository referrals,
        IUserRepository users,
        IReferralQualificationService qualification,
        IAuditService audit,
        INotificationWriter notifications,
        ILogger<AdminReferralAppService> logger)
    {
        _currentUser = currentUser;
        _referrals = referrals;
        _users = users;
        _qualification = qualification;
        _audit = audit;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<AdminReferralOverviewListResponse> ListReferrersAsync(
        string? q, int page, int pageSize, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (referrerIds, total) = await _referrals.SearchReferrerIdsAsync(q, page, pageSize, ct);
        var items = new List<AdminReferralOverviewDto>(referrerIds.Count);
        foreach (var id in referrerIds)
        {
            var dto = await BuildOverviewAsync(id, ct);
            if (dto is not null) items.Add(dto);
        }

        return new AdminReferralOverviewListResponse(items, total, page, pageSize);
    }

    public async Task<AdminReferralDetailResponse> GetDetailAsync(Guid referrerUserId, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var overview = await BuildOverviewAsync(referrerUserId, ct)
                       ?? throw new ApiException(ApiErrorCodes.NotFound, "Referrer not found.", 404);

        var (qualifications, _) = await _referrals.ListByReferrerAsync(referrerUserId, 1, 500, ct);
        var members = new List<ReferralMemberDto>(qualifications.Count);
        foreach (var q in qualifications)
        {
            var referred = await _users.GetByIdAsync(q.ReferredUserId, ct);
            members.Add(ReferralAppService.MapMember(q, referred));
        }

        var (commissions, _) = await _referrals.ListCommissionsAsync(referrerUserId, 1, 20, ct);
        var commissionDtos = new List<ReferralCommissionDto>(commissions.Count);
        foreach (var c in commissions)
        {
            var referred = await _users.GetByIdAsync(c.ReferredUserId, ct);
            commissionDtos.Add(new ReferralCommissionDto(
                c.Id.ToString(),
                c.ReferredUserId.ToString(),
                referred?.FullName ?? referred?.Username ?? referred?.Email,
                c.TradeAmount,
                c.RatePercent,
                c.Tier,
                c.Amount,
                c.CreatedAt));
        }

        var rewards = await _referrals.ListRewardsAsync(referrerUserId, ct);

        return new AdminReferralDetailResponse(
            overview,
            members,
            commissionDtos,
            rewards.Select(ReferralAppService.MapReward).ToList());
    }

    /// <summary>
    /// Manual deposit verification. `met = true` forces the deposit condition regardless of
    /// the observed balance; `met = false` blocks it even if the balance later crosses the
    /// threshold; `met = null` clears the override and returns to the observed signal.
    /// </summary>
    public async Task<ReferralMemberDto> SetDepositOverrideAsync(
        Guid referredUserId, AdminDepositOverrideRequest request, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var qualification = await _referrals.GetQualificationByReferredUserIdAsync(referredUserId, ct)
                            ?? throw new ApiException(ApiErrorCodes.NotFound, "Referral not found.", 404);

        var previous = qualification.Qualified;
        qualification.AdminDepositOverride = request.Met;
        qualification.AdminDepositNote = request.Note;
        qualification.AdminDepositBy = _currentUser.UserId.ToString();
        qualification.AdminDepositAt = DateTimeOffset.UtcNow;
        await _referrals.UpdateQualificationAsync(qualification, ct);

        // Re-run the observation pipeline so Qualified (and any resulting reward) reflects the override immediately.
        await _qualification.ObserveRealBalanceAsync(referredUserId, qualification.PeakRealBalance, ct);
        var updated = await _referrals.GetQualificationByReferredUserIdAsync(referredUserId, ct) ?? qualification;

        await _audit.RecordAsync(
            "referral.deposit_override",
            _currentUser.UserId,
            referredUserId,
            null,
            previous.ToString(),
            updated.Qualified.ToString(),
            request.Note,
            ct);

        var referred = await _users.GetByIdAsync(referredUserId, ct);
        return ReferralAppService.MapMember(updated, referred);
    }

    public async Task<AdminReferralPayoutsResponse> ListPayoutsAsync(
        string? status, int page, int pageSize, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        ReferralPayoutStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ReferralPayoutStatus>(status, ignoreCase: true, out var parsed))
                throw new ApiException(ApiErrorCodes.ValidationError, "status must be Pending, Approved, Rejected, or Paid.");
            filter = parsed;
        }

        var (items, total) = await _referrals.SearchPayoutsAsync(filter, page, pageSize, ct);
        var dtos = new List<AdminReferralPayoutDto>(items.Count);
        foreach (var p in items)
        {
            var user = await _users.GetByIdAsync(p.UserId, ct);
            dtos.Add(new AdminReferralPayoutDto(
                p.Id.ToString(),
                p.UserId.ToString(),
                user?.FullName ?? user?.Username ?? user?.Email,
                p.Amount,
                p.Status.ToString(),
                p.Method,
                p.Destination,
                p.RequestedAt,
                p.DecidedAt,
                p.AdminNote));
        }

        return new AdminReferralPayoutsResponse(dtos, total, page, pageSize);
    }

    public async Task<AdminReferralPayoutDto> DecidePayoutAsync(
        Guid payoutId, AdminPayoutDecisionRequest request, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var payout = await _referrals.GetPayoutByIdAsync(payoutId, ct)
                    ?? throw new ApiException(ApiErrorCodes.NotFound, "Payout request not found.", 404);

        if (payout.Status is ReferralPayoutStatus.Paid or ReferralPayoutStatus.Rejected)
            throw new ApiException(ApiErrorCodes.ValidationError, "This payout is already finalized.", 409);

        var decision = (request.Decision ?? string.Empty).Trim().ToLowerInvariant();
        var previous = payout.Status.ToString();
        var actor = _currentUser.UserId.ToString();

        payout.Status = decision switch
        {
            "approve" => ReferralPayoutStatus.Approved,
            "reject" => ReferralPayoutStatus.Rejected,
            "paid" => ReferralPayoutStatus.Paid,
            _ => throw new ApiException(ApiErrorCodes.ValidationError, "decision must be approve, reject, or paid.")
        };
        payout.DecidedAt = DateTimeOffset.UtcNow;
        payout.DecidedBy = actor;
        payout.AdminNote = request.Note;
        await _referrals.UpdatePayoutAsync(payout, ct);

        await _audit.RecordAsync(
            "referral.payout_decision", _currentUser.UserId, payout.UserId, null, previous, payout.Status.ToString(), request.Note, ct);

        var (title, description) = payout.Status switch
        {
            ReferralPayoutStatus.Approved => ("Payout approved", $"Your ${payout.Amount:0.##} referral payout was approved."),
            ReferralPayoutStatus.Paid => ("Payout sent", $"Your ${payout.Amount:0.##} referral payout has been paid."),
            _ => ("Payout rejected", $"Your ${payout.Amount:0.##} referral payout request was rejected.")
        };
        await _notifications.AddAsync(payout.UserId, "referral-payout", title, description, null, "/referral", ct);

        var user = await _users.GetByIdAsync(payout.UserId, ct);
        return new AdminReferralPayoutDto(
            payout.Id.ToString(),
            payout.UserId.ToString(),
            user?.FullName ?? user?.Username ?? user?.Email,
            payout.Amount,
            payout.Status.ToString(),
            payout.Method,
            payout.Destination,
            payout.RequestedAt,
            payout.DecidedAt,
            payout.AdminNote);
    }

    public async Task<ReferralRewardDto> MarkRewardPaidAsync(Guid rewardId, AdminRewardPaidRequest request, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var reward = await _referrals.GetRewardByIdAsync(rewardId, ct)
                    ?? throw new ApiException(ApiErrorCodes.NotFound, "Reward not found.", 404);

        var previous = reward.Status.ToString();
        reward.Status = ReferralRewardStatus.Paid;
        reward.PaidAt = DateTimeOffset.UtcNow;
        reward.PaidBy = _currentUser.UserId.ToString();
        if (!string.IsNullOrWhiteSpace(request.Note))
            reward.Note = request.Note;
        await _referrals.UpdateRewardAsync(reward, ct);

        await _audit.RecordAsync(
            "referral.reward_paid", _currentUser.UserId, reward.UserId, null, previous, reward.Status.ToString(), request.Note, ct);

        return ReferralAppService.MapReward(reward);
    }

    private async Task<AdminReferralOverviewDto?> BuildOverviewAsync(Guid referrerUserId, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(referrerUserId, ct);
        if (user is null) return null;

        var qualified = await _referrals.CountQualifiedAsync(referrerUserId, ct);
        var total = await _referrals.CountAllAsync(referrerUserId, ct);
        var tier = ReferralTiers.ResolveTier(qualified);
        var commissions = await _referrals.SumCommissionsAsync(referrerUserId, ct);
        var rewards = await _referrals.SumRewardsAsync(
            referrerUserId, new[] { ReferralRewardStatus.Granted, ReferralRewardStatus.Paid }, ct);
        var claimed = await _referrals.SumPayoutsAsync(
            referrerUserId, new[] { ReferralPayoutStatus.Pending, ReferralPayoutStatus.Approved, ReferralPayoutStatus.Paid }, ct);

        return new AdminReferralOverviewDto(
            referrerUserId.ToString(),
            user.FullName ?? user.Username,
            user.Email,
            qualified,
            total,
            tier?.Level ?? 0,
            commissions,
            rewards,
            Math.Max(0m, commissions + rewards - claimed));
    }

    private async Task EnsureAdminAsync(CancellationToken ct)
    {
        if (!_currentUser.IsAdmin)
        {
            throw new ApiException(
                ApiErrorCodes.Forbidden,
                "Your session does not carry the admin role. Sign out and sign in again.",
                403);
        }
        await Task.CompletedTask;
    }
}
