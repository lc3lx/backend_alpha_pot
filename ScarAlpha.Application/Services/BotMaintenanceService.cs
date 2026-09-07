using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Application.Services;

/// <summary>Current maintenance state, as the app and the admin panel see it.</summary>
/// <param name="Active">True while bots are held down and users see the notice.</param>
/// <param name="Message">Optional admin note shown under the heading.</param>
/// <param name="Since">When it was switched on.</param>
public sealed record BotMaintenanceState(bool Active, string? Message, DateTimeOffset? Since);

public interface IBotMaintenanceService
{
    /// <summary>
    /// Cheap, synchronous read for hot paths (the signal worker runs every second and
    /// must not hit the database to ask whether it is allowed to trade).
    /// </summary>
    BotMaintenanceState Current { get; }

    /// <summary>Loads the persisted flag into memory. Called once at startup.</summary>
    Task WarmAsync(CancellationToken ct = default);

    /// <summary>Turns maintenance on or off and persists it.</summary>
    Task<BotMaintenanceState> SetAsync(bool active, string? message, CancellationToken ct = default);
}

/// <summary>
/// The global "bots are down for maintenance" switch.
///
/// <para>Two things depend on it and must never disagree: the worker refuses to place
/// anything while it is on, and every user's bot page shows the maintenance notice. Both
/// read the same in-memory value, which is loaded from the database at startup so an
/// admin's decision survives a deploy — a restart that silently resumed live trading
/// after an admin had stopped everything would be the worst possible failure here.</para>
/// </summary>
public sealed class BotMaintenanceService : IBotMaintenanceService
{
    internal const string ActiveKey = "bot.maintenance.active";
    internal const string MessageKey = "bot.maintenance.message";
    internal const string SinceKey = "bot.maintenance.since";

    private readonly IAppSettingRepository _settings;
    private volatile BotMaintenanceState _current = new(false, null, null);

    public BotMaintenanceService(IAppSettingRepository settings) => _settings = settings;

    public BotMaintenanceState Current => _current;

    public async Task WarmAsync(CancellationToken ct = default)
    {
        var active = await _settings.GetAsync(ActiveKey, ct).ConfigureAwait(false);
        if (!string.Equals(active, "true", StringComparison.OrdinalIgnoreCase))
        {
            _current = new BotMaintenanceState(false, null, null);
            return;
        }

        var message = await _settings.GetAsync(MessageKey, ct).ConfigureAwait(false);
        var since = await _settings.GetAsync(SinceKey, ct).ConfigureAwait(false);
        _current = new BotMaintenanceState(
            true,
            message,
            DateTimeOffset.TryParse(since, out var parsed) ? parsed : null);
    }

    public async Task<BotMaintenanceState> SetAsync(
        bool active,
        string? message,
        CancellationToken ct = default)
    {
        var since = active ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;

        await _settings.SetAsync(ActiveKey, active ? "true" : "false", ct).ConfigureAwait(false);
        await _settings.SetAsync(MessageKey, active ? message : null, ct).ConfigureAwait(false);
        await _settings.SetAsync(SinceKey, since?.ToString("O"), ct).ConfigureAwait(false);

        // Publish only after the write succeeds: reporting "stopped" while the database
        // still says running would come back on the next restart.
        _current = new BotMaintenanceState(active, active ? message : null, since);
        return _current;
    }
}
