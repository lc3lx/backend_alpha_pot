using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

/// <summary>
/// Accrues referral commission when a referred user's real-account trade reaches a final
/// outcome. Called once per settled trade from <c>TradeOutcomeWorker.SettleAsync</c> —
/// the single choke point where every trade becomes Profit/Loss/Tie.
/// </summary>
public sealed class ReferralAccrualService : IReferralAccrualService
{
    private readonly IReferralRepository _referrals;
    private readonly IUserRepository _users;
    private readonly IReferralQualificationService _qualification;
    private readonly ILogger<ReferralAccrualService> _logger;

    public ReferralAccrualService(
        IReferralRepository referrals,
        IUserRepository users,
        IReferralQualificationService qualification,
        ILogger<ReferralAccrualService> logger)
    {
        _referrals = referrals;
        _users = users;
        _qualification = qualification;
        _logger = logger;
    }

    public async Task OnTradeSettledAsync(Trade trade, CancellationToken ct = default)
    {
        if (trade.Status is not (TradeStatus.Profit or TradeStatus.Loss or TradeStatus.Tie))
            return;
        if (trade.AccountType != BinollaAccountType.Real)
            return;

        var user = await _users.GetByIdAsync(trade.UserId, ct);
        if (user is null || user.IsMarketingDemo)
            return;

        var qualification = await _qualification.GetAsync(trade.UserId, ct);
        if (qualification is null)
            return; // not a referred user

        await _qualification.RecordBotTradeActivityAsync(trade.UserId, trade.UpdatedAt, ct);

        // Re-fetch: recording activity may have just flipped Qualified to true.
        qualification = await _qualification.GetAsync(trade.UserId, ct);
        if (qualification is null) return;

        if (ReferralConfig.CommissionFromQualifiedOnly && !qualification.Qualified)
            return;

        var qualifiedCount = await _referrals.CountQualifiedAsync(qualification.ReferrerUserId, ct);
        var tier = ReferralTiers.ResolveTier(qualifiedCount);
        if (tier is null)
            return; // referrer has not reached tier 1 yet

        var commission = new ReferralCommission
        {
            Id = Guid.NewGuid(),
            ReferrerUserId = qualification.ReferrerUserId,
            ReferredUserId = trade.UserId,
            TradeId = trade.Id,
            TradeAmount = trade.Amount,
            RatePercent = tier.Value.RatePercent,
            Tier = tier.Value.Level,
            Amount = Math.Round(trade.Amount * tier.Value.RatePercent / 100m, 8),
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (await _referrals.AddCommissionAsync(commission, ct))
        {
            _logger.LogInformation(
                "Referral commission accrued referrerUserId={ReferrerUserId} referredUserId={ReferredUserId} tradeId={TradeId} amount={Amount}",
                commission.ReferrerUserId, commission.ReferredUserId, commission.TradeId, commission.Amount);
        }
    }
}
