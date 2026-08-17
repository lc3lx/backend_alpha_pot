using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Infrastructure.Access;

namespace ScarAlpha.Infrastructure.Workers;

/// <summary>
/// After API boot (+ migrations), reload Running/Paused bots from users.BotRuntimeJson.
/// </summary>
public sealed class BotRuntimeRestoreHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IBotRuntimeService _runtime;
    private readonly ILogger<BotRuntimeRestoreHostedService> _logger;

    public BotRuntimeRestoreHostedService(
        IServiceScopeFactory scopes,
        IBotRuntimeService runtime,
        ILogger<BotRuntimeRestoreHostedService> logger)
    {
        _scopes = scopes;
        _runtime = runtime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let Program.MigrateAsync finish first.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            await using var scope = _scopes.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var rows = await users.ListWithBotRuntimeAsync(cancellationToken).ConfigureAwait(false);
            var restored = 0;
            foreach (var user in rows)
            {
                var cfg = BotRuntimeService.StoredBotRuntime.TryParse(user.Id, user.BotRuntimeJson);
                if (cfg is null) continue;
                if (cfg.State is not (BotRunState.Running or BotRunState.Paused))
                    continue;
                if (cfg.ResolvedAssets.Count == 0) continue;
                _runtime.RestoreFromPersistence(cfg);
                restored++;
            }

            _logger.LogInformation("Bot runtime restore: {Count} bots reloaded into memory", restored);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bot runtime restore failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
