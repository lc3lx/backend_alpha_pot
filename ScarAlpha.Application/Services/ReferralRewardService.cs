using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

/// <summary>
/// Grants the one-time tier gifts and the iPhone milestone reward once a referrer's
/// qualified-referral count crosses a threshold. Every grant is idempotent — a unique
/// (UserId, Kind, Tier) row means re-evaluating twice never double-grants.
/// </summary>
public sealed class ReferralRewardService : IReferralRewardService
{
    /// <summary>Tier is stored as 0 for the IPhone reward — see <see cref="ReferralReward.Tier"/>.</summary>
    private const int NoTier = 0;

    private readonly IReferralRepository _referrals;
    private readonly INotificationWriter _notifications;
    private readonly ILogger<ReferralRewardService> _logger;

    public ReferralRewardService(
        IReferralRepository referrals,
        INotificationWriter notifications,
        ILogger<ReferralRewardService> logger)
    {
        _referrals = referrals;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task ReevaluateAsync(Guid referrerUserId, CancellationToken ct = default)
    {
        var qualifiedCount = await _referrals.CountQualifiedAsync(referrerUserId, ct);

        foreach (var tier in ReferralTiers.All)
        {
            if (qualifiedCount < tier.MinReferrals) continue;
            if (await _referrals.RewardExistsAsync(referrerUserId, ReferralRewardKind.TierGift, tier.Level, ct)) continue;

            await GrantAsync(
                referrerUserId,
                ReferralRewardKind.TierGift,
                tier.Level,
                tier.GiftUsd,
                $"Tier {tier.Level} referral gift ({tier.MinReferrals}+ qualified referrals).",
                ct);
        }

        if (qualifiedCount >= ReferralTiers.IPhoneQualifiedReferrals
            && !await _referrals.RewardExistsAsync(referrerUserId, ReferralRewardKind.IPhone, NoTier, ct))
        {
            await GrantAsync(
                referrerUserId,
                ReferralRewardKind.IPhone,
                NoTier,
                0m,
                $"iPhone reward ({ReferralTiers.IPhoneQualifiedReferrals}+ qualified referrals).",
                ct);
        }
    }

    private async Task GrantAsync(Guid userId, ReferralRewardKind kind, int tier, decimal amount, string note, CancellationToken ct)
    {
        var reward = new ReferralReward
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            Tier = tier,
            Amount = amount,
            Status = ReferralRewardStatus.Granted,
            GrantedAt = DateTimeOffset.UtcNow,
            Note = note
        };

        if (!await _referrals.AddRewardAsync(reward, ct))
            return; // already granted concurrently — idempotent, nothing more to do

        var (title, description) = kind == ReferralRewardKind.IPhone
            ? ("iPhone reward unlocked!", "You reached 10 qualified referrals and earned the iPhone reward.")
            : ($"Referral tier {tier} unlocked!", $"You reached {ReferralTiers.ByLevel(tier).MinReferrals} qualified referrals and earned a ${amount:0.##} gift.");

        await _notifications.AddAsync(userId, "referral-reward", title, description, null, "/referral", ct);
        _logger.LogInformation(
            "Referral reward granted userId={UserId} kind={Kind} tier={Tier} amount={Amount}",
            userId, kind, tier, amount);
    }
}
