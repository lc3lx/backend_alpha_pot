using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;

namespace ScarAlpha.Application.Services;

public sealed class StrategyAppService
{
    private readonly IStrategyRegistry _registry;

    public StrategyAppService(IStrategyRegistry registry) => _registry = registry;

    public StrategiesResponse ListStrategies()
    {
        var items = _registry.GetStrategies()
            .Select(s => new StrategyDto(
                s.Id,
                s.Name,
                s.Status.ToString(),
                s.Enabled))
            .ToList();
        return new StrategiesResponse(items);
    }

    public void EnsureStrategyEnabled(string strategyId)
    {
        var strategy = _registry.Get(strategyId);
        if (strategy is null)
            throw new ApiException(ApiErrorCodes.StrategyNotFound, $"Strategy '{strategyId}' is not registered.", 404);

        if (!strategy.Enabled)
            throw new ApiException(ApiErrorCodes.StrategyDisabled,
                $"Strategy '{strategy.Name}' is not available yet.", 403);
    }
}
