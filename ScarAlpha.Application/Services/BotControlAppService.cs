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

    public BotControlAppService(
        ICurrentUser currentUser,
        IBotRuntimeService runtime,
        IBotAccessService access,
        IStrategyRegistry strategies)
    {
        _currentUser = currentUser;
        _runtime = runtime;
        _access = access;
        _strategies = strategies;
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
            RequireRunnableStrategy(request.StrategyId)));
    }

    public BotRuntimeDto Pause() => Map(_runtime.Pause(_currentUser.UserId));
    public BotRuntimeDto Stop() => Map(_runtime.Stop(_currentUser.UserId));

    public BotRuntimeDto Apply(BotApplyRequest request) =>
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
            request.StrategyId is null ? null : RequireRunnableStrategy(request.StrategyId)));

    private static BotRuntimeDto Map(BotRuntimeConfig value) =>
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
            value.StrategyId);
}
