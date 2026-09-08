namespace ScarAlpha.Application.Common;

public readonly record struct ReferralTier(int Level, int MinReferrals, decimal RatePercent, decimal GiftUsd);

/// <summary>
/// The five referral tiers. The threshold is read against two different counts depending
/// on what is being decided:
///
/// <list type="bullet">
/// <item><b>Commission</b> — counted against referrals that are ACTIVE (they have started
/// trading). Paid immediately, so a referrer earns while their invitees trade rather than
/// waiting out the qualification window.</item>
/// <item><b>Rewards</b> (tier gifts, the iPhone) — counted against QUALIFIED referrals:
/// deposit ≥ <see cref="ReferralConfig.MinDepositUsd"/>, the 15-day window elapsed, and
/// sustained active trading. See <see cref="ScarAlpha.Domain.Entities.ReferralQualification"/>.</item>
/// </list>
///
/// Below 5 referrals no tier is reached and nothing is earned.
/// </summary>
public static class ReferralTiers
{
    public static readonly IReadOnlyList<ReferralTier> All = new[]
    {
        new ReferralTier(Level: 1, MinReferrals: 5, RatePercent: 0.15m, GiftUsd: 25m),
        new ReferralTier(Level: 2, MinReferrals: 10, RatePercent: 0.25m, GiftUsd: 75m),
        new ReferralTier(Level: 3, MinReferrals: 20, RatePercent: 0.40m, GiftUsd: 200m),
        new ReferralTier(Level: 4, MinReferrals: 50, RatePercent: 0.75m, GiftUsd: 500m),
        new ReferralTier(Level: 5, MinReferrals: 100, RatePercent: 1.00m, GiftUsd: 1000m),
    };

    /// <summary>Qualified-referral count required for the iPhone reward.</summary>
    public const int IPhoneQualifiedReferrals = 10;

    /// <summary>The highest tier reached by this many qualified referrals, or null below tier 1.</summary>
    public static ReferralTier? ResolveTier(int qualifiedCount)
    {
        ReferralTier? current = null;
        foreach (var tier in All)
        {
            if (qualifiedCount >= tier.MinReferrals)
                current = tier;
        }
        return current;
    }

    public static ReferralTier ByLevel(int level) => All.First(t => t.Level == level);
}

/// <summary>
/// Tunable referral-program parameters, loaded once from configuration at startup
/// (see DependencyInjection.AddScarAlphaInfrastructure) so they can change without a rebuild.
/// </summary>
public static class ReferralConfig
{
    /// <summary>Minimum observed real-account balance for a referred user's deposit condition.</summary>
    public static decimal MinDepositUsd { get; set; } = 350m;
    /// <summary>Days a referred user must stay in the bot before they can qualify their referrer.</summary>
    public static int QualificationDays { get; set; } = 15;
    /// <summary>Distinct active trading days required within the qualification window.</summary>
    public static int MinActiveDaysInWindow { get; set; } = 5;
    /// <summary>How often the background watcher polls live balances for deposit detection.</summary>
    public static int BalancePollMinutes { get; set; } = 30;
    /// <summary>
    /// When true, only a fully qualified referred user's trades generate commission.
    ///
    /// <para>Off by default: commission is meant to be immediate, so a referrer starts
    /// earning as soon as the people they invited trade. Turning this back on delays every
    /// payout by the qualification window.</para>
    /// </summary>
    public static bool CommissionFromQualifiedOnly { get; set; } = false;
    /// <summary>Minimum amount a user may request in one payout.</summary>
    public static decimal MinPayoutUsd { get; set; } = 20m;
    /// <summary>Base URL used to build the shareable referral link.</summary>
    public static string WebBaseUrl { get; set; } = "https://www.scaralphaai.com";
}
