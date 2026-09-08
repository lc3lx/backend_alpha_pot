using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Abstractions;

public interface IReferralRepository
{
    Task<User?> GetUserByReferralCodeAsync(string code, CancellationToken ct = default);
    Task<bool> ReferralCodeExistsAsync(string code, CancellationToken ct = default);

    Task<ReferralQualification?> GetQualificationByReferredUserIdAsync(Guid referredUserId, CancellationToken ct = default);
    Task AddQualificationAsync(ReferralQualification qualification, CancellationToken ct = default);
    Task UpdateQualificationAsync(ReferralQualification qualification, CancellationToken ct = default);
    /// <summary>Count of this referrer's referred users whose qualification is currently true.</summary>
    Task<int> CountQualifiedAsync(Guid referrerUserId, CancellationToken ct = default);
    /// <summary>Count of every user this referrer has ever referred, qualified or not.</summary>
    Task<int> CountAllAsync(Guid referrerUserId, CancellationToken ct = default);
    Task<(IReadOnlyList<ReferralQualification> Items, int Total)> ListByReferrerAsync(
        Guid referrerUserId, int page, int pageSize, CancellationToken ct = default);
    /// <summary>Not-yet-qualified rows, for the background balance watcher.</summary>
    Task<IReadOnlyList<ReferralQualification>> ListDepositWatchAsync(int take, CancellationToken ct = default);
    Task<(IReadOnlyList<Guid> ReferrerIds, int Total)> SearchReferrerIdsAsync(
        string? q, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Inserts a commission entry. Returns false when TradeId already has one (idempotent).</summary>
    Task<bool> AddCommissionAsync(ReferralCommission commission, CancellationToken ct = default);
    Task<decimal> SumCommissionsAsync(Guid referrerUserId, CancellationToken ct = default);
    Task<(IReadOnlyList<ReferralCommission> Items, int Total)> ListCommissionsAsync(
        Guid referrerUserId, int page, int pageSize, CancellationToken ct = default);

    Task<bool> RewardExistsAsync(Guid userId, ReferralRewardKind kind, int tier, CancellationToken ct = default);
    /// <summary>Inserts a reward. Returns false when the (userId, kind, tier) grant already exists (idempotent).</summary>
    Task<bool> AddRewardAsync(ReferralReward reward, CancellationToken ct = default);
    Task<decimal> SumRewardsAsync(Guid userId, IReadOnlyCollection<ReferralRewardStatus> statuses, CancellationToken ct = default);
    Task<IReadOnlyList<ReferralReward>> ListRewardsAsync(Guid userId, CancellationToken ct = default);
    Task<ReferralReward?> GetRewardByIdAsync(Guid id, CancellationToken ct = default);
    Task UpdateRewardAsync(ReferralReward reward, CancellationToken ct = default);

    Task AddPayoutAsync(ReferralPayout payout, CancellationToken ct = default);
    Task<decimal> SumPayoutsAsync(Guid userId, IReadOnlyCollection<ReferralPayoutStatus> statuses, CancellationToken ct = default);
    Task<bool> HasPendingPayoutAsync(Guid userId, CancellationToken ct = default);
    Task<(IReadOnlyList<ReferralPayout> Items, int Total)> ListPayoutsByUserAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default);
    Task<(IReadOnlyList<ReferralPayout> Items, int Total)> SearchPayoutsAsync(
        ReferralPayoutStatus? status, int page, int pageSize, CancellationToken ct = default);
    Task<ReferralPayout?> GetPayoutByIdAsync(Guid id, CancellationToken ct = default);
    Task UpdatePayoutAsync(ReferralPayout payout, CancellationToken ct = default);
}

/// <summary>
/// Owns a referred user's qualification progress: the observed real-balance deposit signal
/// and bot-trading activity, and re-evaluates whether they now qualify their referrer.
/// </summary>
public interface IReferralQualificationService
{
    /// <summary>Feed a live real-account balance reading (from an already-fetched broker balance — never fetches on its own).</summary>
    Task ObserveRealBalanceAsync(Guid referredUserId, decimal realBalance, CancellationToken ct = default);
    /// <summary>Record that a real-account bot trade happened for this referred user at this time.</summary>
    Task RecordBotTradeActivityAsync(Guid referredUserId, DateTimeOffset tradeAt, CancellationToken ct = default);
    Task<ReferralQualification?> GetAsync(Guid referredUserId, CancellationToken ct = default);
}

/// <summary>Grants tier-gift and iPhone rewards once a referrer's qualified-referral count crosses a threshold.</summary>
public interface IReferralRewardService
{
    Task ReevaluateAsync(Guid referrerUserId, CancellationToken ct = default);
}

/// <summary>Accrues referral commission when a referred user's real-account trade settles.</summary>
public interface IReferralAccrualService
{
    Task OnTradeSettledAsync(Trade trade, CancellationToken ct = default);
}
