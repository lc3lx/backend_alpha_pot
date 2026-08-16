using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;

namespace ScarAlpha.Application.Services;

public sealed class BotControlAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBotRuntimeService _runtime;
    private readonly IBotAccessService _access;

    public BotControlAppService(ICurrentUser currentUser, IBotRuntimeService runtime, IBotAccessService access)
    {
        _currentUser = currentUser;
        _runtime = runtime;
        _access = access;
    }

    public BotRuntimeDto Get() => Map(_runtime.Get(_currentUser.UserId));

    public async Task<BotRuntimeDto> StartAsync(BotStartRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "Select a trading pair before starting the bot.");
        var access = await _access.CheckAsync(_currentUser.UserId, ct);
        AccountAppService.EnsureAllowed(access);
        return Map(_runtime.Start(_currentUser.UserId, request.Asset, request.Amount, request.DurationSeconds, request.DailyProfitTarget, request.DailyLossLimit, request.AutoStopAtProfit, request.AutoStopAtLoss, request.SignalConfirmationEnabled, request.RiskLevel, request.NotificationsEnabled));
    }

    public BotRuntimeDto Pause() => Map(_runtime.Pause(_currentUser.UserId));
    public BotRuntimeDto Stop() => Map(_runtime.Stop(_currentUser.UserId));
    public BotRuntimeDto Apply(BotApplyRequest request) =>
        Map(_runtime.Apply(_currentUser.UserId, request.Asset, request.Amount, request.DurationSeconds, request.DailyProfitTarget, request.DailyLossLimit, request.AutoStopAtProfit, request.AutoStopAtLoss, request.SignalConfirmationEnabled, request.RiskLevel, request.NotificationsEnabled));

    private static BotRuntimeDto Map(BotRuntimeConfig value) =>
        new(value.State.ToString(), value.Asset, value.Amount, value.DurationSeconds, value.DailyProfitTarget, value.DailyLossLimit, value.UpdatedAt, value.AutoStopAtProfit, value.AutoStopAtLoss, value.SignalConfirmationEnabled, value.RiskLevel, value.NotificationsEnabled);
}
