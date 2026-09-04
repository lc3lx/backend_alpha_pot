using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Access;

public sealed class BotRuntimeService : IBotRuntimeService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<Guid, BotRuntimeConfig> _states = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BotRuntimeService> _logger;

    public BotRuntimeService(IServiceScopeFactory scopes, ILogger<BotRuntimeService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public BotRuntimeConfig Get(Guid userId)
    {
        if (_states.TryGetValue(userId, out var state))
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug281dcf.Write(
                "B",
                "BotRuntimeService.Get",
                "get_from_memory",
                new
                {
                    userId = userId.ToString(),
                    state = state.State.ToString(),
                    strategyId = state.StrategyId,
                    stakeMode = state.StakeMode,
                    assetCount = state.ResolvedAssets.Count
                });
            // #endregion
            return state;
        }

        var defaults = New(userId, BotRunState.Stopped, Array.Empty<string>(), 25m, 25m, 300, 50m, 30m, stakeMode: StakeProgression.RedSignalPro);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug281dcf.Write(
            "B",
            "BotRuntimeService.Get",
            "get_defaults_no_hydrate",
            new
            {
                userId = userId.ToString(),
                strategyId = defaults.StrategyId,
                stakeMode = defaults.StakeMode,
                assetCount = defaults.ResolvedAssets.Count
            });
        // #endregion
        return defaults;
    }

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
        bool notificationsEnabled = true,
        string strategyId = "rsi",
        string stakeMode = StakeProgression.RedSignalPro) =>
        Set(
            userId,
            BotRunState.Running,
            BotAssetList.Normalize(null, assets),
            amount,
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
            stopReason: null,
            strategyId: strategyId,
            stakeMode: StakeProgression.NormalizeMode(stakeMode),
            persist: true);

    public BotRuntimeConfig Pause(Guid userId)
    {
        var current = Get(userId);
        return Set(
            userId,
            BotRunState.Paused,
            current.ResolvedAssets,
            current.Amount,
            current.BaseAmount,
            current.DurationSeconds,
            current.DailyProfitTarget,
            current.DailyLossLimit,
            current.AutoStopAtProfit,
            current.AutoStopAtLoss,
            current.SignalConfirmationEnabled,
            current.RiskLevel,
            current.NotificationsEnabled,
            pnlSessionStartedAt: current.PnlSessionStartedAt,
            stopReason: current.StopReason,
            strategyId: current.StrategyId,
            stakeMode: current.StakeMode,
            persist: true);
    }

    public BotRuntimeConfig Stop(Guid userId, string? stopReason = null)
    {
        var current = Get(userId);
        return Set(
            userId,
            BotRunState.Stopped,
            current.ResolvedAssets,
            current.Amount,
            current.BaseAmount,
            current.DurationSeconds,
            current.DailyProfitTarget,
            current.DailyLossLimit,
            current.AutoStopAtProfit,
            current.AutoStopAtLoss,
            current.SignalConfirmationEnabled,
            current.RiskLevel,
            current.NotificationsEnabled,
            pnlSessionStartedAt: current.PnlSessionStartedAt,
            stopReason: stopReason,
            strategyId: current.StrategyId,
            stakeMode: current.StakeMode,
            persist: true);
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
        IReadOnlyList<string>? assets = null,
        string? strategyId = null,
        string? stakeMode = null)
    {
        var current = Get(userId);
        var nextAssets = assets is not null || asset is not null
            ? BotAssetList.Normalize(asset, assets)
            : current.ResolvedAssets;

        var stakeChanged = stakeMode is not null;
        var nextStake = stakeChanged
            ? StakeProgression.NormalizeMode(stakeMode)
            : current.StakeMode;

        decimal nextBase;
        decimal nextAmount;
        if (amount.HasValue)
        {
            nextBase = amount.Value;
            nextAmount = amount.Value;
        }
        else
        {
            nextBase = current.BaseAmount;
            nextAmount = stakeChanged ? current.EffectiveBaseAmount : current.Amount;
        }

        var next = Set(
            userId,
            current.State,
            nextAssets,
            nextAmount,
            nextBase,
            durationSeconds ?? current.DurationSeconds,
            dailyProfitTarget ?? current.DailyProfitTarget,
            dailyLossLimit ?? current.DailyLossLimit,
            autoStopAtProfit ?? current.AutoStopAtProfit,
            autoStopAtLoss ?? current.AutoStopAtLoss,
            signalConfirmationEnabled ?? current.SignalConfirmationEnabled,
            riskLevel ?? current.RiskLevel,
            notificationsEnabled ?? current.NotificationsEnabled,
            pnlSessionStartedAt: current.PnlSessionStartedAt,
            stopReason: current.StopReason,
            strategyId: strategyId ?? current.StrategyId,
            stakeMode: nextStake,
            persist: true);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug281dcf.Write(
            "C",
            "BotRuntimeService.Apply",
            "apply_persisted",
            new
            {
                userId = userId.ToString(),
                state = next.State.ToString(),
                strategyId = next.StrategyId,
                stakeMode = next.StakeMode,
                assetCount = next.ResolvedAssets.Count,
                amount = next.Amount
            });
        // #endregion
        return next;
    }

    public BotRuntimeConfig ApplyStakeAfterOutcome(Guid userId, decimal lastTradeAmount, bool wasLoss)
    {
        var current = Get(userId);
        if (current.State is not (BotRunState.Running or BotRunState.Paused))
            return current;

        var nextAmount = wasLoss
            ? StakeProgression.CalculateNextAfterLoss(current.StakeMode, current.EffectiveBaseAmount, lastTradeAmount)
            : StakeProgression.ResetAfterWin(current.EffectiveBaseAmount);

        if (nextAmount == current.Amount)
            return current;

        return Set(
            userId,
            current.State,
            current.ResolvedAssets,
            nextAmount,
            current.EffectiveBaseAmount,
            current.DurationSeconds,
            current.DailyProfitTarget,
            current.DailyLossLimit,
            current.AutoStopAtProfit,
            current.AutoStopAtLoss,
            current.SignalConfirmationEnabled,
            current.RiskLevel,
            current.NotificationsEnabled,
            pnlSessionStartedAt: current.PnlSessionStartedAt,
            stopReason: current.StopReason,
            strategyId: current.StrategyId,
            stakeMode: current.StakeMode,
            persist: true);
    }

    public IReadOnlyList<BotRuntimeConfig> ListKnown() =>
        _states.Values.OrderByDescending(x => x.UpdatedAt).ToList();

    public void RestoreFromPersistence(BotRuntimeConfig config)
    {
        _states[config.UserId] = config;
    }

    private BotRuntimeConfig Set(
        Guid userId,
        BotRunState state,
        IReadOnlyList<string> assets,
        decimal amount,
        decimal baseAmount,
        int durationSeconds,
        decimal dailyProfitTarget,
        decimal dailyLossLimit,
        bool autoStopAtProfit,
        bool autoStopAtLoss,
        bool signalConfirmationEnabled,
        string riskLevel,
        bool notificationsEnabled,
        DateTimeOffset? pnlSessionStartedAt,
        string? stopReason,
        bool persist,
        string strategyId = "rsi",
        string stakeMode = StakeProgression.RedSignalPro)
    {
        if (amount <= 0 || amount > 100_000m)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (baseAmount <= 0 || baseAmount > 100_000m)
            throw new ArgumentOutOfRangeException(nameof(baseAmount));
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
            baseAmount,
            durationSeconds,
            dailyProfitTarget,
            dailyLossLimit,
            autoStopAtProfit,
            autoStopAtLoss,
            signalConfirmationEnabled,
            riskLevel,
            notificationsEnabled,
            pnlSessionStartedAt,
            stopReason,
            strategyId,
            stakeMode);
        _states[userId] = next;
        if (persist)
            QueuePersist(next);
        return next;
    }

    private void QueuePersist(BotRuntimeConfig cfg)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var user = await users.GetByIdAsync(cfg.UserId).ConfigureAwait(false);
                if (user is null) return;
                user.BotRuntimeJson = JsonSerializer.Serialize(StoredBotRuntime.From(cfg), JsonOpts);
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await users.UpdateAsync(user).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist bot runtime for {UserId}", cfg.UserId);
            }
        });
    }

    private static BotRuntimeConfig New(
        Guid userId,
        BotRunState state,
        IReadOnlyList<string> assets,
        decimal amount,
        decimal baseAmount,
        int duration,
        decimal profitTarget,
        decimal lossLimit,
        bool autoStopAtProfit = true,
        bool autoStopAtLoss = true,
        bool signalConfirmationEnabled = true,
        string riskLevel = "risk-medium",
        bool notificationsEnabled = true,
        DateTimeOffset? pnlSessionStartedAt = null,
        string? stopReason = null,
        string strategyId = "rsi",
        string stakeMode = StakeProgression.RedSignalPro)
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
            stopReason,
            NormalizeStrategy(strategyId),
            baseAmount,
            StakeProgression.NormalizeMode(stakeMode));
    }

    /// <summary>Unknown ids fall back to the always-available RSI strategy.</summary>
    internal static string NormalizeStrategy(string? strategyId)
    {
        var id = strategyId?.Trim();
        if (string.Equals(id, "ema", StringComparison.OrdinalIgnoreCase)) return "ema";
        if (string.Equals(id, "smart", StringComparison.OrdinalIgnoreCase)) return "smart";
        if (string.Equals(id, "alt5", StringComparison.OrdinalIgnoreCase)) return "alt5";
        return "rsi";
    }

    public sealed record StoredBotRuntime(
        string State,
        IReadOnlyList<string> Assets,
        decimal Amount,
        int DurationSeconds,
        decimal DailyProfitTarget,
        decimal DailyLossLimit,
        bool AutoStopAtProfit,
        bool AutoStopAtLoss,
        bool SignalConfirmationEnabled,
        string RiskLevel,
        bool NotificationsEnabled,
        DateTimeOffset? PnlSessionStartedAt,
        string? StopReason,
        string StrategyId = "rsi",
        decimal BaseAmount = 0m,
        string StakeMode = StakeProgression.RedSignalPro)
    {
        public static StoredBotRuntime From(BotRuntimeConfig c) => new(
            c.State.ToString(),
            c.ResolvedAssets.ToList(),
            c.Amount,
            c.DurationSeconds,
            c.DailyProfitTarget,
            c.DailyLossLimit,
            c.AutoStopAtProfit,
            c.AutoStopAtLoss,
            c.SignalConfirmationEnabled,
            c.RiskLevel,
            c.NotificationsEnabled,
            c.PnlSessionStartedAt,
            c.StopReason,
            c.StrategyId,
            c.EffectiveBaseAmount,
            c.StakeMode);

        public BotRuntimeConfig ToConfig(Guid userId)
        {
            var state = Enum.TryParse<BotRunState>(State, true, out var s) ? s : BotRunState.Stopped;
            var assets = BotAssetList.Normalize(null, Assets);
            var amount = Amount <= 0 ? 25m : Amount;
            var baseAmount = BaseAmount <= 0 ? amount : BaseAmount;
            return new BotRuntimeConfig(
                userId,
                state,
                assets.Count > 0 ? assets[0] : null,
                amount,
                DurationSeconds is < 5 or > 3600 ? 300 : DurationSeconds,
                DailyProfitTarget < 0 ? 50m : DailyProfitTarget,
                DailyLossLimit < 0 ? 30m : DailyLossLimit,
                DateTimeOffset.UtcNow,
                AutoStopAtProfit,
                AutoStopAtLoss,
                SignalConfirmationEnabled,
                RiskLevel: RiskLevel is "risk-low" or "risk-medium" or "risk-high" ? RiskLevel : "risk-medium",
                NotificationsEnabled,
                assets,
                PnlSessionStartedAt,
                StopReason,
                NormalizeStrategy(StrategyId),
                baseAmount,
                StakeProgression.NormalizeMode(StakeMode));
        }

        public static BotRuntimeConfig? TryParse(Guid userId, string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var stored = JsonSerializer.Deserialize<StoredBotRuntime>(json, JsonOpts);
                return stored?.ToConfig(userId);
            }
            catch
            {
                return null;
            }
        }
    }
}
