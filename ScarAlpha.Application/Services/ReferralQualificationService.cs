using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Domain.Entities;

namespace ScarAlpha.Application.Services;

/// <summary>
/// Tracks one referred user's progress toward qualifying their referrer: the deposit
/// signal (peak observed real balance), the 15-day window, and active trading days.
/// Every mutation re-evaluates <see cref="ReferralQualification.Qualified"/> and, on the
/// false→true transition, asks <see cref="IReferralRewardService"/> to re-check the
/// referrer's tier and rewards.
/// </summary>
public sealed class ReferralQualificationService : IReferralQualificationService
{
    private readonly IReferralRepository _referrals;
    private readonly IReferralRewardService _rewards;
    private readonly ILogger<ReferralQualificationService> _logger;

    public ReferralQualificationService(
        IReferralRepository referrals,
        IReferralRewardService rewards,
        ILogger<ReferralQualificationService> logger)
    {
        _referrals = referrals;
        _rewards = rewards;
        _logger = logger;
    }

    public async Task ObserveRealBalanceAsync(Guid referredUserId, decimal realBalance, CancellationToken ct = default)
    {
        var qualification = await _referrals.GetQualificationByReferredUserIdAsync(referredUserId, ct);
        if (qualification is null) return; // not a referred user — nothing to track

        var dirty = false;
        if (realBalance > qualification.PeakRealBalance)
        {
            qualification.PeakRealBalance = realBalance;
            dirty = true;
        }

        if (!qualification.DepositMet
            && qualification.AdminDepositOverride != false
            && qualification.PeakRealBalance >= ReferralConfig.MinDepositUsd)
        {
            qualification.DepositMet = true;
            qualification.DepositMetAt = DateTimeOffset.UtcNow;
            dirty = true;
        }

        if (!dirty) return;
        await EvaluateAndSaveAsync(qualification, ct);
    }

    public async Task RecordBotTradeActivityAsync(Guid referredUserId, DateTimeOffset tradeAt, CancellationToken ct = default)
    {
        var qualification = await _referrals.GetQualificationByReferredUserIdAsync(referredUserId, ct);
        if (qualification is null) return;

        var day = new DateTimeOffset(tradeAt.UtcDateTime.Date, TimeSpan.Zero);
        qualification.FirstBotTradeAt ??= tradeAt;
        qualification.LastBotTradeAt = tradeAt;
        qualification.BotTradeCount++;
        if (qualification.LastActiveDayUtc != day)
        {
            qualification.ActiveDaysCount++;
            qualification.LastActiveDayUtc = day;
        }

        await EvaluateAndSaveAsync(qualification, ct);
    }

    public Task<ReferralQualification?> GetAsync(Guid referredUserId, CancellationToken ct = default) =>
        _referrals.GetQualificationByReferredUserIdAsync(referredUserId, ct);

    private async Task EvaluateAndSaveAsync(ReferralQualification qualification, CancellationToken ct)
    {
        var wasQualified = qualification.Qualified;

        var depositOk = qualification.AdminDepositOverride ?? qualification.DepositMet;
        var windowElapsed = DateTimeOffset.UtcNow - qualification.WindowStartedAt >= TimeSpan.FromDays(ReferralConfig.QualificationDays);
        var activeEnough = qualification.ActiveDaysCount >= ReferralConfig.MinActiveDaysInWindow;

        qualification.Qualified = depositOk && windowElapsed && activeEnough;
        if (qualification.Qualified)
            qualification.QualifiedAt ??= DateTimeOffset.UtcNow;
        else
            qualification.QualifiedAt = null;

        qualification.UpdatedAt = DateTimeOffset.UtcNow;
        await _referrals.UpdateQualificationAsync(qualification, ct);

        if (!wasQualified && qualification.Qualified)
        {
            _logger.LogInformation(
                "Referral qualified referredUserId={ReferredUserId} referrerUserId={ReferrerUserId}",
                qualification.ReferredUserId, qualification.ReferrerUserId);
            await _rewards.ReevaluateAsync(qualification.ReferrerUserId, ct);
        }
    }
}
