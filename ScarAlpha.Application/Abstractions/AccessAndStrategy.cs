using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Abstractions;

public enum BotAccessState
{
    Allowed,
    BinollaNotConnected,
    AdminApprovalRequired,
    NotEligible,
    SessionExpired
}

public sealed record BotAccessResult(
    BotAccessState Access,
    bool BinollaConnected,
    bool AdminApproved,
    string AccountType,
    string ApprovalStatus);

public interface IBotAccessService
{
    Task<BotAccessResult> CheckAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Restores in-process Binolla sessions from encrypted SSIDs after API restart.
/// Never exposes or logs SSID material.
/// </summary>
public interface IBinollaSessionRestorer
{
    /// <summary>Completes when the startup restore wave finishes (success or partial).</summary>
    Task WhenInitialRestoreCompleted { get; }

    /// <summary>
    /// Restore all approved users that still have a Connected link in the database.
    /// Safe to call multiple times; already-live sessions are skipped.
    /// </summary>
    Task RestoreApprovedSessionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Best-effort restore for a single approved/pending user (lazy path after idle eviction / mid-restore).
    /// Returns true when a live Connected/Reconnected session exists afterwards.
    /// </summary>
    Task<bool> TryRestoreUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Clears the sticky auth-failure skip so a fresh credential login can restore again.
    /// </summary>
    void ClearAuthFailure(Guid userId);

    /// <summary>
    /// Kick a non-blocking restore if the user is not live. Safe to call on every status poll.
    /// </summary>
    void EnsureBackgroundRestore(Guid userId);
}

public enum StrategyCatalogStatus
{
    Active,
    ComingSoon
}

public sealed record StrategyInfo(
    string Id,
    string Name,
    StrategyCatalogStatus Status,
    bool Enabled);

public enum BotRunState
{
    Stopped,
    Running,
    Paused
}

public sealed record BotRuntimeConfig(
    Guid UserId,
    BotRunState State,
    string? Asset,
    decimal Amount,
    int DurationSeconds,
    decimal DailyProfitTarget,
    decimal DailyLossLimit,
    DateTimeOffset UpdatedAt,
    bool AutoStopAtProfit = true,
    bool AutoStopAtLoss = true,
    bool SignalConfirmationEnabled = true,
    string RiskLevel = "risk-medium",
    bool NotificationsEnabled = true,
    IReadOnlyList<string>? Assets = null,
    /// <summary>PnL for daily limits is counted only from this timestamp (set on each Start).</summary>
    DateTimeOffset? PnlSessionStartedAt = null,
    /// <summary>Why the bot last stopped — e.g. DAILY_PROFIT_TARGET_REACHED.</summary>
    string? StopReason = null,
    /// <summary>Which strategy the bot executes: "rsi" or "ema".</summary>
    string StrategyId = "rsi",
    /// <summary>Configured base stake — progression resets here after a win.</summary>
    decimal BaseAmount = 0m,
    /// <summary>Stake progression mode (technical indicator): red-signal-pro, alpha-momentum, etc.</summary>
    string StakeMode = "red-signal-pro",
    /// <summary>UI market scope: global-indicators | binolla-market | all-markets.</summary>
    string MarketTypeId = "all-markets")
{
    public decimal EffectiveBaseAmount => BaseAmount > 0 ? BaseAmount : Amount;
    /// <summary>Selected pairs to analyze (falls back to single Asset).</summary>
    public IReadOnlyList<string> ResolvedAssets =>
        Assets is { Count: > 0 }
            ? Assets
            : string.IsNullOrWhiteSpace(Asset)
                ? Array.Empty<string>()
                : new[] { Asset };
}

public interface IBotRuntimeService
{
    BotRuntimeConfig Get(Guid userId);
    BotRuntimeConfig Start(
        Guid userId,
        IReadOnlyList<string> assets,
        decimal amount = 25m,
        int durationSeconds = 300,
        decimal dailyProfitTarget = 50m,
        decimal dailyLossLimit = 30m,
        bool autoStopAtProfit = true,
        bool autoStopAtLoss = true,
        bool signalConfirmationEnabled = true,
        string riskLevel = "risk-medium",
        bool notificationsEnabled = true,
        string strategyId = "rsi",
        string stakeMode = "red-signal-pro",
        string? marketTypeId = null);
    BotRuntimeConfig Pause(Guid userId);
    BotRuntimeConfig Stop(Guid userId, string? stopReason = null);
    BotRuntimeConfig Apply(
        Guid userId,
        string? asset,
        decimal? amount,
        int? durationSeconds,
        decimal? dailyProfitTarget,
        decimal? dailyLossLimit,
        bool? autoStopAtProfit = null,
        bool? autoStopAtLoss = null,
        bool? signalConfirmationEnabled = null,
        string? riskLevel = null,
        bool? notificationsEnabled = null,
        IReadOnlyList<string>? assets = null,
        string? strategyId = null,
        string? stakeMode = null,
        string? marketTypeId = null);
    /// <summary>Adjust bot stake after a bot trade settles (win resets, loss progresses).</summary>
    BotRuntimeConfig ApplyStakeAfterOutcome(Guid userId, decimal lastTradeAmount, bool wasLoss);
    IReadOnlyList<BotRuntimeConfig> ListKnown();
    /// <summary>Load persisted runtime into memory (API startup). Does not rewrite DB.</summary>
    void RestoreFromPersistence(BotRuntimeConfig config);
}

public interface IStrategyRegistry
{
    IReadOnlyList<StrategyInfo> GetStrategies();
    StrategyInfo? Get(string strategyId);
}

public interface IAuditService
{
    Task RecordAsync(
        string action,
        Guid actorUserId,
        Guid? targetUserId,
        Guid? targetBinollaLinkId,
        string? previousState,
        string? newState,
        string? detail = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<AuditEvent>> ListForTargetUserAsync(Guid targetUserId, int take, CancellationToken ct = default);

    Task<(IReadOnlyList<AuditEvent> Items, int Total)> SearchAsync(
        Guid? targetUserId,
        string? action,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken ct = default);
}

public interface INotificationRepository
{
    Task AddAsync(UserNotification notification, CancellationToken ct = default);
    Task<IReadOnlyList<UserNotification>> ListByUserAsync(Guid userId, int take, CancellationToken ct = default);
    Task<UserNotification?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<int> CountUnreadAsync(Guid userId, CancellationToken ct = default);
    Task UpdateAsync(UserNotification notification, CancellationToken ct = default);
    Task MarkAllReadAsync(Guid userId, CancellationToken ct = default);
    Task<(IReadOnlyList<UserNotification> Items, int Total)> SearchAdminAsync(
        Guid? userId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}

public interface INotificationWriter
{
    Task AddAsync(
        Guid userId,
        string variant,
        string title,
        string description,
        Guid? tradeId = null,
        string? actionPath = null,
        CancellationToken ct = default);
}
