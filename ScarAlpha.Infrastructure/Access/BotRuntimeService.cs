using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.Access;

public sealed class BotRuntimeService : IBotRuntimeService
{
    private readonly ConcurrentDictionary<Guid, BotRuntimeConfig> _states = new();

    public BotRuntimeConfig Get(Guid userId) => _states.TryGetValue(userId, out var state)
        ? state
        : New(userId, BotRunState.Stopped, null, 25m, 300, 50m, 30m);

    public BotRuntimeConfig Start(Guid userId, string asset, decimal amount = 25m, int durationSeconds = 300, decimal dailyProfitTarget = 50m, decimal dailyLossLimit = 30m) =>
        Set(userId, BotRunState.Running, asset, amount, durationSeconds, dailyProfitTarget, dailyLossLimit);

    public BotRuntimeConfig Pause(Guid userId)
    {
        var current = Get(userId);
        return Set(userId, BotRunState.Paused, current.Asset, current.Amount, current.DurationSeconds, current.DailyProfitTarget, current.DailyLossLimit);
    }

    public BotRuntimeConfig Stop(Guid userId)
    {
        var current = Get(userId);
        return Set(userId, BotRunState.Stopped, current.Asset, current.Amount, current.DurationSeconds, current.DailyProfitTarget, current.DailyLossLimit);
    }

    public BotRuntimeConfig Apply(Guid userId, string? asset, decimal? amount, int? durationSeconds, decimal? dailyProfitTarget, decimal? dailyLossLimit)
    {
        var current = Get(userId);
        return Set(userId, current.State, asset ?? current.Asset, amount ?? current.Amount, durationSeconds ?? current.DurationSeconds, dailyProfitTarget ?? current.DailyProfitTarget, dailyLossLimit ?? current.DailyLossLimit);
    }

    public IReadOnlyList<BotRuntimeConfig> ListKnown() =>
        _states.Values.OrderByDescending(x => x.UpdatedAt).ToList();

    private BotRuntimeConfig Set(Guid userId, BotRunState state, string? asset, decimal amount, int durationSeconds, decimal dailyProfitTarget, decimal dailyLossLimit)
    {
        if (amount <= 0 || amount > 100_000m)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (durationSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (dailyProfitTarget < 0 || dailyLossLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(dailyProfitTarget));
        var next = New(userId, state, asset?.Trim(), amount, durationSeconds, dailyProfitTarget, dailyLossLimit);
        _states[userId] = next;
        return next;
    }

    private static BotRuntimeConfig New(Guid userId, BotRunState state, string? asset, decimal amount, int duration, decimal profitTarget, decimal lossLimit) =>
        new(userId, state, asset, amount, duration, profitTarget, lossLimit, DateTimeOffset.UtcNow);
}
