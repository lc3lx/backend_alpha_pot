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
}
