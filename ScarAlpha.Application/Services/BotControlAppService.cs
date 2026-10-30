using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;

namespace ScarAlpha.Application.Services;

public sealed class BotControlAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBotRuntimeService _runtime;
    private readonly IBotAccessService _access;
    private readonly IStrategyRegistry _strategies;
    private readonly IBotMaintenanceService _maintenance;

    public BotControlAppService(
        ICurrentUser currentUser,
        IBotRuntimeService runtime,
        IBotAccessService access,
        IStrategyRegistry strategies,
        IBotMaintenanceService maintenance)
    {
        _currentUser = currentUser;
        _runtime = runtime;
        _access = access;
        _strategies = strategies;
        _maintenance = maintenance;
    }

    /// <summary>
    /// Rejects unknown or not-yet-released strategies instead of quietly falling back to
    /// RSI — running a different strategy than the one you asked for is worse than an error.
    /// </summary>
    private string RequireRunnableStrategy(string? strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
            return "rsi";

        var id = strategyId.Trim();
        var info = _strategies.Get(id);
        if (info is null)
            throw new ApiException(ApiErrorCodes.ValidationError, $"Unknown strategy '{id}'.");
        if (!info.Enabled || info.Status != StrategyCatalogStatus.Active)
            throw new ApiException(ApiErrorCodes.ValidationError, $"Strategy '{info.Name}' is not available yet.");

        return info.Id;
    }

    public BotRuntimeDto Get() => Map(_runtime.Get(_currentUser.UserId));

    public async Task<BotRuntimeDto> StartAsync(BotStartRequest request, CancellationToken ct)
    {
        var assets = BotAssetList.Normalize(request.Asset, request.Assets);
        if (assets.Count == 0)
            throw new ApiException(ApiErrorCodes.ValidationError, "Select at least one trading pair before starting the bot.");
        var access = await _access.CheckAsync(_currentUser.UserId, ct);
        AccountAppService.EnsureAllowed(access);
        var strategyId = RequireRunnableStrategy(request.StrategyId);
        if (IPhoneJourneyStrategy.Is(strategyId))
            request = JourneyLimits(request);
        return Map(_runtime.Start(
            _currentUser.UserId,
            assets,
            request.Amount,
            request.DurationSeconds,
            request.DailyProfitTarget,
            request.DailyLossLimit,
            request.AutoStopAtProfit,
            request.AutoStopAtLoss,
            request.SignalConfirmationEnabled,
            request.RiskLevel,
            request.NotificationsEnabled,
            strategyId,
            request.StakeMode,
            request.MarketTypeId));
    }

    /// <summary>
    /// iPhone Journey runs on fixed terms whatever the client sends: a flat $1-2 stake with
    /// no progression, a five-minute option, and BOTH session limits switched on. The
    /// target and the loss limit are the user's to choose, but a journey can never start
    /// without them — an open-ended run of a rule with no measured edge is exactly how
    /// an account is drained slowly instead of stopped early.
    /// </summary>
    private static BotStartRequest JourneyLimits(BotStartRequest request)
    {
        static decimal Bound(decimal value, decimal fallback, decimal max) =>
            value <= 0 ? fallback : Math.Min(value, max);

        return request with
        {
            Amount = IPhoneJourneyStrategy.ClampStake(request.Amount),
            DurationSeconds = IPhoneJourneyStrategy.ExpiryCandles * 60,
            StakeMode = StakeProgression.RedSignalPro,
            DailyProfitTarget = Bound(request.DailyProfitTarget, 20m, 1_000m),
            DailyLossLimit = Bound(request.DailyLossLimit, 20m, 1_000m),
            AutoStopAtProfit = true,
            AutoStopAtLoss = true,
        };
    }

    public BotRuntimeDto Pause() => Map(_runtime.Pause(_currentUser.UserId));
    public BotRuntimeDto Stop() => Map(_runtime.Stop(_currentUser.UserId));

    public BotRuntimeDto Apply(BotApplyRequest request)
    {
        // A running journey keeps its fixed terms: Apply can change the target, the loss
        // limit or the pairs, never the stake progression, the expiry, or switch a limit off.
        var effective = request.StrategyId ?? _runtime.Get(_currentUser.UserId).StrategyId;
        if (IPhoneJourneyStrategy.Is(effective))
        {
            request = request with
            {
                Amount = request.Amount is decimal a ? IPhoneJourneyStrategy.ClampStake(a) : null,
                DurationSeconds = request.DurationSeconds is null ? null : IPhoneJourneyStrategy.ExpiryCandles * 60,
                StakeMode = StakeProgression.RedSignalPro,
                AutoStopAtProfit = true,
                AutoStopAtLoss = true,
            };
        }
        return ApplyCore(request);
    }

    private BotRuntimeDto ApplyCore(BotApplyRequest request) =>
        Map(_runtime.Apply(
            _currentUser.UserId,
            request.Asset,
            request.Amount,
            request.DurationSeconds,
            request.DailyProfitTarget,
            request.DailyLossLimit,
            request.AutoStopAtProfit,
            request.AutoStopAtLoss,
            request.SignalConfirmationEnabled,
            request.RiskLevel,
            request.NotificationsEnabled,
            request.Assets,
            request.StrategyId is null ? null : RequireRunnableStrategy(request.StrategyId),
            request.StakeMode,
            request.MarketTypeId));

    private BotRuntimeDto Map(BotRuntimeConfig value) =>
        new(
            value.State.ToString(),
            value.Asset,
            value.Amount,
            value.DurationSeconds,
            value.DailyProfitTarget,
            value.DailyLossLimit,
            value.UpdatedAt,
            value.AutoStopAtProfit,
            value.AutoStopAtLoss,
            value.SignalConfirmationEnabled,
            value.RiskLevel,
            value.NotificationsEnabled,
            value.ResolvedAssets,
            value.PnlSessionStartedAt,
            value.StopReason,
            value.StrategyId,
            value.EffectiveBaseAmount,
            value.StakeMode,
            value.MarketTypeId,
            CurrentMaintenance());

    /// <summary>
    /// The global stop, if it is up. Null when trading is running normally, so the app
    /// only has to check for presence.
    /// </summary>
    private BotMaintenanceDto? CurrentMaintenance()
    {
        var state = _maintenance.Current;
        return state.Active ? new BotMaintenanceDto(true, state.Message, state.Since) : null;
    }
}

