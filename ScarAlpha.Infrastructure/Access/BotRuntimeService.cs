using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Access;

public sealed class BotRuntimeService : IBotRuntimeService
{
    private readonly ConcurrentDictionary<Guid, BotRuntimeConfig> _states = new();

    public BotRuntimeConfig Get(Guid userId) => _states.TryGetValue(userId, out var state)
        ? state
        : New(userId, BotRunState.Stopped, Array.Empty<string>(), 25m, 300, 50m, 30m);

    public BotRuntimeConfig Start(
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
        bool notificationsEnabled = true) =>
        // User approval to run again: clear stop reason and reset PnL session to zero.
        Set(
            userId,
            BotRunState.Running,
            BotAssetList.Normalize(null, assets),
            amount,
            durationSeconds,
            dailyProfitTarget,
            dailyLossLimit,
            autoStopAtProfit,
            autoStopAtLoss,
            signalConfirmationEnabled,
            riskLevel,
            notificationsEnabled,
            pnlSessionStartedAt: DateTimeOffset.UtcNow,
            stopReason: null);

    public BotRuntimeConfig Pause(Guid userId)
    {
        var current = Get(userId);
        return Set(
            userId,
            BotRunState.Paused,
            current.ResolvedAssets,
            current.Amount,
            current.DurationSeconds,
            current.DailyProfitTarget,
            current.DailyLossLimit,
            current.AutoStopAtProfit,
            current.AutoStopAtLoss,
            current.SignalConfirmationEnabled,
            current.RiskLevel,
            current.NotificationsEnabled,
            pnlSessionStartedAt: current.PnlSessionStartedAt,
            stopReason: current.StopReason);
    }

    public BotRuntimeConfig Stop(Guid userId, string? stopReason = null)
    {
        var current = Get(userId);
        return Set(
            userId,
            BotRunState.Stopped,
            current.ResolvedAssets,
            current.Amount,
            current.DurationSeconds,
            current.DailyProfitTarget,
            current.DailyLossLimit,
            current.AutoStopAtProfit,
            current.AutoStopAtLoss,
            current.SignalConfirmationEnabled,
            current.RiskLevel,
            current.NotificationsEnabled,
            pnlSessionStartedAt: current.PnlSessionStartedAt,
            stopReason: stopReason);
    }

    public BotRuntimeConfig Apply(
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
        IReadOnlyList<string>? assets = null)
    {
        var current = Get(userId);
        var nextAssets = assets is not null || asset is not null
            ? BotAssetList.Normalize(asset, assets)
            : current.ResolvedAssets;
        return Set(
            userId,
            current.State,
            nextAssets,
            amount ?? current.Amount,
            durationSeconds ?? current.DurationSeconds,
            dailyProfitTarget ?? current.DailyProfitTarget,
            dailyLossLimit ?? current.DailyLossLimit,
            autoStopAtProfit ?? current.AutoStopAtProfit,
            autoStopAtLoss ?? current.AutoStopAtLoss,
            signalConfirmationEnabled ?? current.SignalConfirmationEnabled,
            riskLevel ?? current.RiskLevel,
            notificationsEnabled ?? current.NotificationsEnabled,
            pnlSessionStartedAt: current.PnlSessionStartedAt,
            stopReason: current.StopReason);
    }

    public IReadOnlyList<BotRuntimeConfig> ListKnown() =>
        _states.Values.OrderByDescending(x => x.UpdatedAt).ToList();

    private BotRuntimeConfig Set(
        Guid userId,
        BotRunState state,
        IReadOnlyList<string> assets,
        decimal amount,
        int durationSeconds,
        decimal dailyProfitTarget,
        decimal dailyLossLimit,
        bool autoStopAtProfit,
        bool autoStopAtLoss,
        bool signalConfirmationEnabled,
        string riskLevel,
        bool notificationsEnabled,
        DateTimeOffset? pnlSessionStartedAt,
        string? stopReason)
    {
        if (amount <= 0 || amount > 100_000m)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (durationSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (dailyProfitTarget < 0 || dailyLossLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(dailyProfitTarget));
        if (riskLevel is not ("risk-low" or "risk-medium" or "risk-high"))
            throw new ArgumentOutOfRangeException(nameof(riskLevel));
        var normalized = BotAssetList.Normalize(null, assets);
        var next = New(
            userId,
            state,
            normalized,
            amount,
            durationSeconds,
            dailyProfitTarget,
            dailyLossLimit,
            autoStopAtProfit,
            autoStopAtLoss,
            signalConfirmationEnabled,
            riskLevel,
            notificationsEnabled,
            pnlSessionStartedAt,
            stopReason);
        _states[userId] = next;
        return next;
    }

    private static BotRuntimeConfig New(
        Guid userId,
        BotRunState state,
        IReadOnlyList<string> assets,
        decimal amount,
        int duration,
        decimal profitTarget,
        decimal lossLimit,
        bool autoStopAtProfit = true,
        bool autoStopAtLoss = true,
        bool signalConfirmationEnabled = true,
        string riskLevel = "risk-medium",
        bool notificationsEnabled = true,
        DateTimeOffset? pnlSessionStartedAt = null,
        string? stopReason = null)
    {
        var list = BotAssetList.Normalize(null, assets);
        return new BotRuntimeConfig(
            userId,
            state,
            list.Count > 0 ? list[0] : null,
            amount,
            duration,
            profitTarget,
            lossLimit,
            DateTimeOffset.UtcNow,
            autoStopAtProfit,
            autoStopAtLoss,
            signalConfirmationEnabled,
            riskLevel,
            notificationsEnabled,
            list,
            pnlSessionStartedAt,
            stopReason);
    }
}
