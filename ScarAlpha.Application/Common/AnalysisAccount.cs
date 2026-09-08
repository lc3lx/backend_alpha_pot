namespace ScarAlpha.Application.Common;

/// <summary>
/// The account whose Binolla session is used to READ the market for everyone.
///
/// <para>Market data is identical for every account, so analysing it once and sharing the
/// result is both correct and the only thing that scales: at 1000 users, scanning per
/// account would be 1000 identical passes over the same candles every minute.</para>
///
/// <para>Until now that one session was borrowed from whichever user happened to be first
/// in the cohort. That works, but it ties the whole fleet's analysis to one customer's
/// connection — if their socket degrades, everyone's scan degrades with it, and the
/// "who is analysing" answer changes minute to minute. Naming a dedicated account here
/// makes the data source explicit, stable, and independent of who is signed in.</para>
///
/// <para>Configured through <c>Strategy:AnalysisUserId</c>. When it is unset or its
/// session is not usable the worker falls back to borrowing a user session, so the bot
/// keeps trading rather than stopping because a system account was misconfigured.</para>
/// </summary>
public static class AnalysisAccount
{
    private static Guid? _userId;

    /// <summary>The dedicated analysis account, or null when none is configured.</summary>
    public static Guid? UserId
    {
        get => _userId;
        set => _userId = value == Guid.Empty ? null : value;
    }

    /// <summary>True when a dedicated account has been configured.</summary>
    public static bool IsConfigured => _userId is not null;

    /// <summary>
    /// The order in which sessions should be tried as the market data source: the
    /// dedicated account first, then the given fallbacks with it removed so it is never
    /// attempted twice.
    /// </summary>
    public static IReadOnlyList<Guid> ScanOrder(IEnumerable<Guid> fallbacks)
    {
        var rest = fallbacks.Where(id => id != _userId).ToList();
        return _userId is Guid dedicated ? new[] { dedicated }.Concat(rest).ToList() : rest;
    }
}
