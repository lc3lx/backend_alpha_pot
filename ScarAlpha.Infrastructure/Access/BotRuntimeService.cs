using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.Access;

public sealed class BotRuntimeService : IBotRuntimeService
{
    private readonly ConcurrentDictionary<Guid, BotRuntimeConfig> _states = new();

    public BotRuntimeConfig Get(Guid userId) => _states.TryGetValue(userId, out var state)
        ? state
        : New(userId, BotRunState.Stopped, null, 25m, 300, 50m, 30m);

    public BotRuntimeConfig Start(Guid userId, string asset, decimal amount = 25m, int durationSeconds = 300, decimal dailyProfitTarget = 50m, decimal dailyLossLimit = 30m, bool autoStopAtProfit = true, bool autoStopAtLoss = true, bool signalConfirmationEnabled = true, string riskLevel = "risk-medium", bool notificationsEnabled = true) =>
        Set(userId, BotRunState.Running, asset, amount, durationSeconds, dailyProfitTarget, dailyLossLimit, autoStopAtProfit, autoStopAtLoss, signalConfirmationEnabled, riskLevel, notificationsEnabled);

    public BotRuntimeConfig Pause(Guid userId)
    {
        var current = Get(userId);
        return Set(userId, BotRunState.Paused, current.Asset, current.Amount, current.DurationSeconds, current.DailyProfitTarget, current.DailyLossLimit, current.AutoStopAtProfit, current.AutoStopAtLoss, current.SignalConfirmationEnabled, current.RiskLevel, current.NotificationsEnabled);
    }

    public BotRuntimeConfig Stop(Guid userId)
    {
        var current = Get(userId);
        return Set(userId, BotRunState.Stopped, current.Asset, current.Amount, current.DurationSeconds, current.DailyProfitTarget, current.DailyLossLimit, current.AutoStopAtProfit, current.AutoStopAtLoss, current.SignalConfirmationEnabled, current.RiskLevel, current.NotificationsEnabled);
    }

    public BotRuntimeConfig Apply(Guid userId, string? asset, decimal? amount, int? durationSeconds, decimal? dailyProfitTarget, decimal? dailyLossLimit, bool? autoStopAtProfit = null, bool? autoStopAtLoss = null, bool? signalConfirmationEnabled = null, string? riskLevel = null, bool? notificationsEnabled = null)
    {
        var current = Get(userId);
        return Set(userId, current.State, asset ?? current.Asset, amount ?? current.Amount, durationSeconds ?? current.DurationSeconds, dailyProfitTarget ?? current.DailyProfitTarget, dailyLossLimit ?? current.DailyLossLimit, autoStopAtProfit ?? current.AutoStopAtProfit, autoStopAtLoss ?? current.AutoStopAtLoss, signalConfirmationEnabled ?? current.SignalConfirmationEnabled, riskLevel ?? current.RiskLevel, notificationsEnabled ?? current.NotificationsEnabled);
    }

    public IReadOnlyList<BotRuntimeConfig> ListKnown() =>
        _states.Values.OrderByDescending(x => x.UpdatedAt).ToList();

    private BotRuntimeConfig Set(Guid userId, BotRunState state, string? asset, decimal amount, int durationSeconds, decimal dailyProfitTarget, decimal dailyLossLimit, bool autoStopAtProfit, bool autoStopAtLoss, bool signalConfirmationEnabled, string riskLevel, bool notificationsEnabled)
    {
        if (amount <= 0 || amount > 100_000m)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (durationSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (dailyProfitTarget < 0 || dailyLossLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(dailyProfitTarget));
        if (riskLevel is not ("risk-low" or "risk-medium" or "risk-high"))
            throw new ArgumentOutOfRangeException(nameof(riskLevel));
        var next = New(userId, state, asset?.Trim(), amount, durationSeconds, dailyProfitTarget, dailyLossLimit, autoStopAtProfit, autoStopAtLoss, signalConfirmationEnabled, riskLevel, notificationsEnabled);
        _states[userId] = next;
        return next;
    }

    private static BotRuntimeConfig New(Guid userId, BotRunState state, string? asset, decimal amount, int duration, decimal profitTarget, decimal lossLimit, bool autoStopAtProfit = true, bool autoStopAtLoss = true, bool signalConfirmationEnabled = true, string riskLevel = "risk-medium", bool notificationsEnabled = true) =>
        new(userId, state, asset, amount, duration, profitTarget, lossLimit, DateTimeOffset.UtcNow, autoStopAtProfit, autoStopAtLoss, signalConfirmationEnabled, riskLevel, notificationsEnabled);
}
