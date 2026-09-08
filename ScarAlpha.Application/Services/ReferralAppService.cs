using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

/// <summary>User-facing referral program: share code/link, summary, members, commissions, rewards, payout requests.</summary>
public sealed class ReferralAppService
{
    // Excludes visually ambiguous characters (0/O, 1/I).
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 8;

    private readonly ICurrentUser _currentUser;
    private readonly IUserRepository _users;
    private readonly IReferralRepository _referrals;
    private readonly ILogger<ReferralAppService> _logger;

    public ReferralAppService(
        ICurrentUser currentUser,
        IUserRepository users,
        IReferralRepository referrals,
        ILogger<ReferralAppService> logger)
    {
        _currentUser = currentUser;
        _users = users;
        _referrals = referrals;
        _logger = logger;
    }

    /// <summary>
    /// Attaches a new account to its referrer by code. Best-effort: an unknown or missing
    /// code never blocks signup, it is simply ignored (logged). Called once, right after
    /// the new user is persisted, from every signup path (email, Telegram, Binolla).
    /// </summary>
    public async Task AttachReferrerAsync(Guid newUserId, string? referralCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(referralCode)) return;
        var code = NormalizeCode(referralCode);
        if (code.Length == 0) return;

        var referrer = await _referrals.GetUserByReferralCodeAsync(code, ct);
        if (referrer is null)
        {
            _logger.LogInformation("Referral code not found at signup code={Code} newUserId={NewUserId}", code, newUserId);
            return;
        }
        if (referrer.Id == newUserId)
            return; // self-referral guard (defensive — cannot normally happen at signup)

        var user = await _users.GetByIdAsync(newUserId, ct);
        if (user is null || user.ReferredByUserId is not null)
            return; // already attached elsewhere — never move a referral

        var now = DateTimeOffset.UtcNow;
        user.ReferredByUserId = referrer.Id;
        user.ReferredAt = now;
        user.UpdatedAt = now;
        await _users.UpdateAsync(user, ct);

        await _referrals.AddQualificationAsync(new ReferralQualification
        {
            Id = Guid.NewGuid(),
            ReferredUserId = user.Id,
            ReferrerUserId = referrer.Id,
            WindowStartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        }, ct);

        _logger.LogInformation(
            "Referral attached referredUserId={ReferredUserId} referrerUserId={ReferrerUserId}",
            user.Id, referrer.Id);
    }

    public async Task<string> GetOrCreateReferralCodeAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct)
                   ?? throw new ApiException(ApiErrorCodes.Unauthorized, "User not found.", 401);
        if (!string.IsNullOrWhiteSpace(user.ReferralCode))
            return user.ReferralCode;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = GenerateCode();
            if (await _referrals.ReferralCodeExistsAsync(candidate, ct))
                continue;

            user.ReferralCode = candidate;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(user, ct);
            return candidate;
        }

        throw new ApiException(ApiErrorCodes.ValidationError, "Could not generate a referral code — try again.", 500);
    }

    public async Task<ReferralSummaryResponse> GetSummaryAsync(CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var code = await GetOrCreateReferralCodeAsync(userId, ct);

        var qualifiedCount = await _referrals.CountQualifiedAsync(userId, ct);
        var totalCount = await _referrals.CountAllAsync(userId, ct);
        var activeCount = await _referrals.CountActiveAsync(userId, ct);

        // The displayed tier must be the one being PAID, which follows referrals that are
        // trading. Showing the qualified-count tier here would quote a rate that does not
        // match what actually lands in the balance. Qualified count still drives the
        // rewards progress shown alongside it.
        var currentTier = ReferralTiers.ResolveTier(activeCount);
        var nextTier = ReferralTiers.All.FirstOrDefault(t => t.MinReferrals > activeCount);
        var hasNextTier = nextTier.Level != 0;

        var commissions = await _referrals.SumCommissionsAsync(userId, ct);
        var grantedRewards = await _referrals.SumRewardsAsync(
            userId, new[] { ReferralRewardStatus.Granted, ReferralRewardStatus.Paid }, ct);
        var claimed = await _referrals.SumPayoutsAsync(
            userId, new[] { ReferralPayoutStatus.Pending, ReferralPayoutStatus.Approved, ReferralPayoutStatus.Paid }, ct);
        var paidOut = await _referrals.SumPayoutsAsync(userId, new[] { ReferralPayoutStatus.Paid }, ct);
        var available = Math.Max(0m, commissions + grantedRewards - claimed);

        var link = $"{ReferralConfig.WebBaseUrl.TrimEnd('/')}/signup?ref={code}";
        var telegramShare = $"https://t.me/share/url?url={Uri.EscapeDataString(link)}";

        return new ReferralSummaryResponse(
            ReferralCode: code,
            ReferralLink: link,
            TelegramShareLink: telegramShare,
            QualifiedReferrals: qualifiedCount,
            TotalReferrals: totalCount,
            PendingReferrals: Math.Max(0, totalCount - qualifiedCount),
            CurrentTier: currentTier?.Level ?? 0,
            CurrentRatePercent: currentTier?.RatePercent ?? 0m,
            NextTier: hasNextTier ? MapTier(nextTier, qualifiedCount, currentTier) : null,
            ReferralsToNextTier: hasNextTier ? Math.Max(0, nextTier.MinReferrals - qualifiedCount) : 0,
            TotalCommissionEarned: commissions,
            TotalRewardsEarned: grantedRewards,
            TotalPaidOut: paidOut,
            AvailableBalance: available,
            MinPayoutUsd: ReferralConfig.MinPayoutUsd,
            IPhoneQualifiedReferrals: qualifiedCount,
            IPhoneTarget: ReferralTiers.IPhoneQualifiedReferrals,
            IPhoneEligible: qualifiedCount >= ReferralTiers.IPhoneQualifiedReferrals,
            Tiers: ReferralTiers.All.Select(t => MapTier(t, qualifiedCount, currentTier)).ToList());
    }

    public async Task<ReferralMembersResponse> GetMembersAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await _referrals.ListByReferrerAsync(_currentUser.UserId, page, pageSize, ct);

        var dtos = new List<ReferralMemberDto>(items.Count);
        foreach (var q in items)
        {
            var referred = await _users.GetByIdAsync(q.ReferredUserId, ct);
            dtos.Add(MapMember(q, referred));
        }

        return new ReferralMembersResponse(dtos, total, page, pageSize);
    }

    public async Task<ReferralCommissionsResponse> GetCommissionsAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await _referrals.ListCommissionsAsync(_currentUser.UserId, page, pageSize, ct);

        var dtos = new List<ReferralCommissionDto>(items.Count);
        foreach (var c in items)
        {
            var referred = await _users.GetByIdAsync(c.ReferredUserId, ct);
            dtos.Add(new ReferralCommissionDto(
                c.Id.ToString(),
                c.ReferredUserId.ToString(),
                referred?.FullName ?? referred?.Username ?? referred?.Email,
                c.TradeAmount,
                c.RatePercent,
                c.Tier,
                c.Amount,
                c.CreatedAt));
        }

        return new ReferralCommissionsResponse(dtos, total, page, pageSize);
    }

    public async Task<ReferralRewardsResponse> GetRewardsAsync(CancellationToken ct)
    {
        var items = await _referrals.ListRewardsAsync(_currentUser.UserId, ct);
        return new ReferralRewardsResponse(items.Select(MapReward).ToList());
    }

    public async Task<ReferralPayoutsResponse> GetPayoutsAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await _referrals.ListPayoutsByUserAsync(_currentUser.UserId, page, pageSize, ct);
        return new ReferralPayoutsResponse(items.Select(MapPayout).ToList(), total, page, pageSize);
    }

    public async Task<ReferralPayoutDto> RequestPayoutAsync(ReferralPayoutRequest request, CancellationToken ct)
    {
        if (request is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Request body is required.");
        if (request.Amount <= 0)
            throw new ApiException(ApiErrorCodes.ValidationError, "amount must be greater than zero.");
        if (request.Amount < ReferralConfig.MinPayoutUsd)
            throw new ApiException(
                ApiErrorCodes.PayoutBelowMinimum,
                $"Minimum payout is ${ReferralConfig.MinPayoutUsd:0.##}.",
                400);
        if (string.IsNullOrWhiteSpace(request.Method))
            throw new ApiException(ApiErrorCodes.ValidationError, "method is required.");
        if (string.IsNullOrWhiteSpace(request.Destination))
            throw new ApiException(ApiErrorCodes.ValidationError, "destination is required.");

        var userId = _currentUser.UserId;
        if (await _referrals.HasPendingPayoutAsync(userId, ct))
            throw new ApiException(ApiErrorCodes.PayoutPendingExists, "You already have a pending payout request.", 409);

        var commissions = await _referrals.SumCommissionsAsync(userId, ct);
        var grantedRewards = await _referrals.SumRewardsAsync(
            userId, new[] { ReferralRewardStatus.Granted, ReferralRewardStatus.Paid }, ct);
        var claimed = await _referrals.SumPayoutsAsync(
            userId, new[] { ReferralPayoutStatus.Pending, ReferralPayoutStatus.Approved, ReferralPayoutStatus.Paid }, ct);
        var available = commissions + grantedRewards - claimed;

        if (request.Amount > available)
            throw new ApiException(
                ApiErrorCodes.PayoutInsufficientBalance,
                "Requested amount exceeds your available referral balance.",
                400);

        var payout = new ReferralPayout
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Amount = request.Amount,
            Status = ReferralPayoutStatus.Pending,
            Method = request.Method.Trim(),
            Destination = request.Destination.Trim(),
            RequestedAt = DateTimeOffset.UtcNow
        };
        await _referrals.AddPayoutAsync(payout, ct);
        return MapPayout(payout);
    }

    internal static ReferralTierDto MapTier(ReferralTier tier, int qualifiedCount, ReferralTier? currentTier) => new(
        tier.Level,
        tier.MinReferrals,
        tier.RatePercent,
        tier.GiftUsd,
        Reached: qualifiedCount >= tier.MinReferrals,
        IsCurrent: currentTier?.Level == tier.Level);

    internal static ReferralMemberDto MapMember(ReferralQualification q, User? referred) => new(
        UserId: q.ReferredUserId.ToString(),
        DisplayName: referred?.FullName ?? referred?.Username ?? referred?.Email,
        ReferredAt: q.WindowStartedAt,
        DepositMet: q.AdminDepositOverride ?? q.DepositMet,
        ActiveDaysCount: q.ActiveDaysCount,
        RequiredActiveDays: ReferralConfig.MinActiveDaysInWindow,
        QualificationDays: ReferralConfig.QualificationDays,
        WindowEndsAt: q.WindowStartedAt.AddDays(ReferralConfig.QualificationDays),
        Qualified: q.Qualified,
        QualifiedAt: q.QualifiedAt);

    internal static ReferralRewardDto MapReward(ReferralReward r) => new(
        r.Id.ToString(),
        r.Kind.ToString(),
        r.Kind == ReferralRewardKind.IPhone ? null : r.Tier,
        r.Amount,
        r.Status.ToString(),
        r.GrantedAt,
        r.PaidAt,
        r.Note);

    private static ReferralPayoutDto MapPayout(ReferralPayout p) => new(
        p.Id.ToString(),
        p.Amount,
        p.Status.ToString(),
        p.Method,
        p.Destination,
        p.RequestedAt,
        p.DecidedAt,
        p.AdminNote);

    private static string GenerateCode()
    {
        Span<char> buffer = stackalloc char[CodeLength];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(buffer);
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();
}
