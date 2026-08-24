using Microsoft.AspNetCore.Mvc;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Application.Services;

namespace ScarAlpha.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/telegram", async (
            [FromBody] TelegramAuthRequest request,
            AuthAppService auth,
            CancellationToken ct) =>
        {
            var result = await auth.AuthenticateTelegramAsync(request, ct);
            return Results.Ok(new { accessToken = result.AccessToken, userId = result.UserId });
        });

        group.MapPost("/register", async (
            [FromBody] EmailAuthRequest request,
            AuthAppService auth,
            CancellationToken ct) =>
        {
            var result = await auth.RegisterAsync(request, ct);
            return Results.Ok(new { accessToken = result.AccessToken, userId = result.UserId });
        });

        group.MapPost("/login", async (
            [FromBody] EmailAuthRequest request,
            AuthAppService auth,
            CancellationToken ct) =>
        {
            var result = await auth.LoginAsync(request, ct);
            return Results.Ok(new { accessToken = result.AccessToken, userId = result.UserId });
        });

        group.MapPost("/demo-login", async (
            [FromBody] EmailAuthRequest request,
            AuthAppService auth,
            CancellationToken ct) =>
        {
            var result = await auth.DemoLoginAsync(request, ct);
            return Results.Ok(new { accessToken = result.AccessToken, userId = result.UserId });
        });

        group.MapPost("/change-password", async (
            [FromBody] ChangePasswordRequest request,
            AuthAppService auth,
            CancellationToken ct) =>
        {
            await auth.ChangePasswordAsync(request, ct);
            return Results.Ok(new { changed = true });
        }).RequireAuthorization();

        // Bind Telegram Mini App identity to the current JWT user (email/demo → bot).
        group.MapPost("/link-telegram", async (
            [FromBody] TelegramAuthRequest request,
            AuthAppService auth,
            CancellationToken ct) =>
        {
            var result = await auth.LinkTelegramAsync(request, ct);
            return Results.Ok(new { accessToken = result.AccessToken, userId = result.UserId });
        }).RequireAuthorization();

        return group;
    }
}

public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Me").RequireAuthorization();
        group.MapGet("/me", async (MeAppService me, CancellationToken ct) =>
            Results.Ok(await me.GetMeAsync(ct)));
        group.MapPut("/me", async (
            [FromBody] UpdateProfileRequest request,
            MeAppService me,
            CancellationToken ct) =>
            Results.Ok(await me.UpdateAsync(request, ct)));
        return group;
    }
}

public static class BinollaEndpoints
{
    public static RouteGroupBuilder MapBinollaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/binolla").WithTags("Binolla").RequireAuthorization();

        group.MapPost("/connect", async ([FromBody] BinollaConnectRequest request, BinollaAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.ConnectAsync(request, ct)))
            .RequireRateLimiting("connect");

        group.MapPost("/login", async ([FromBody] BinollaCredentialRequest request, BinollaAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.LoginWithCredentialsAsync(request, ct)))
            .RequireRateLimiting("connect");

        group.MapPost("/reconnect", async (BinollaAppService svc, CancellationToken ct) =>
        {
            var result = await svc.TryReloginFromStoredCredentialsAsync(ct);
            return result is null
                ? Results.Conflict(new { code = "BINOLLA_CREDENTIALS_MISSING", message = "Saved Binolla login not found. Sign in with email/password once." })
                : Results.Ok(result);
        }).RequireRateLimiting("connect");

        group.MapPost("/signup", async ([FromBody] BinollaCredentialRequest request, BinollaAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.SignUpWithCredentialsAsync(request, ct)))
            .RequireRateLimiting("connect");

        group.MapGet("/status", async (BinollaAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetStatusAsync(ct)));

        group.MapGet("/balance", async (BinollaAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetBalanceAsync(ct)));

        group.MapPost("/account-type", async ([FromBody] BinollaAccountTypeRequest request, BinollaAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.ChangeAccountTypeAsync(request, ct)));

        group.MapPost("/disconnect", async (BinollaAppService svc, CancellationToken ct) =>
        {
            await svc.DisconnectAsync(ct);
            return Results.Ok(new { disconnected = true });
        });

        return group;
    }
}

public static class MarketEndpoints
{
    public static RouteGroupBuilder MapMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/market").WithTags("Market").RequireAuthorization();

        group.MapGet("/assets", async (MarketAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAssetsAsync(ct)));

        group.MapGet("/price/{asset}", async (string asset, MarketAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetPriceAsync(asset, ct)));

        group.MapGet("/candles/{asset}", async (
            string asset,
            MarketAppService svc,
            CancellationToken ct,
            [FromQuery] int period = 60) =>
            Results.Ok(await svc.GetCandlesAsync(asset, period, ct)));

        return group;
    }
}

public static class TradeEndpoints
{
    public static RouteGroupBuilder MapTradeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/trades").WithTags("Trades").RequireAuthorization();

        group.MapPost("/", async (
            HttpRequest http,
            [FromBody] PlaceTradeRequest request,
            TradeAppService svc,
            CancellationToken ct) =>
        {
            if (!http.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key))
                return Results.BadRequest(new ApiErrorResponse("VALIDATION_ERROR", "Idempotency-Key header is required."));

            var trade = await svc.PlaceTradeAsync(request, key.ToString(), ct);
            return Results.Ok(trade);
        });

        group.MapGet("/", async (
            TradeAppService svc,
            CancellationToken ct,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? status = null,
            [FromQuery] string? asset = null) =>
            Results.Ok(await svc.ListTradesAsync(page, pageSize, status, asset, ct)));

        group.MapGet("/{id:guid}", async (Guid id, TradeAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetTradeAsync(id, ct)));

        return group;
    }
}

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account").WithTags("Account").RequireAuthorization();
        group.MapGet("/status", async (AccountAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetStatusAsync(ct)));
        group.MapGet("/subscription", async (AccountAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetSubscriptionAsync(ct)));
        group.MapGet("/activation-history", async (AccountAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetActivationHistoryAsync(ct)));
        return group;
    }
}

public static class StrategyEndpoints
{
    public static RouteGroupBuilder MapStrategyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/strategies").WithTags("Strategies").RequireAuthorization();
        group.MapGet("/", (StrategyAppService svc) => Results.Ok(svc.ListStrategies()));

        // Live equivalent of the Pine corner table for the EMA 9/21 + RSI strategy.
        group.MapGet("/ema/stats", (EmaRsiTradeTracker tracker, ICurrentUser user) =>
            Results.Ok(tracker.GetStats(user.UserId)));
        group.MapPost("/ema/stats/reset", (EmaRsiTradeTracker tracker, ICurrentUser user) =>
        {
            tracker.Reset(user.UserId);
            return Results.Ok(tracker.GetStats(user.UserId));
        });
        // Kept at the historical /rsi/ path the Mini App already polls, but it now
        // evaluates whichever strategy the bot is configured for.
        group.MapGet("/rsi/signal/{asset}", async (
            string asset,
            RsiSignalAppService svc,
            IBotRuntimeService botRuntime,
            ICurrentUser currentUser,
            CancellationToken ct,
            [FromQuery] int period = 60,
            [FromQuery] int rsiLength = 14,
            [FromQuery] decimal oversold = 25m,
            [FromQuery] decimal overbought = 75m,
            [FromQuery] int backtestCandles = 200,
            [FromQuery] int expiryCandles = 5,
            [FromQuery] decimal minimumSuccessRate = 75m,
            [FromQuery] bool autoExecute = false) =>
        {
            _ = (oversold, overbought, period);
            var strategyId = botRuntime.Get(currentUser.UserId).StrategyId;

            // The strategy owns its timeframe. The `period` query parameter predates
            // multi-timeframe strategies and callers still send the old 60s default, so
            // honouring it would 500 every poll for any strategy that is not 1-minute.
            var timeframe = StrategyTimeframes.For(strategyId);

            var options = new RsiStrategyOptions(
                Period: rsiLength,
                Oversold: RsiEntryLevels.CallMax,
                Overbought: RsiEntryLevels.PutMin,
                TimeframeSeconds: timeframe,
                BacktestCandleCount: backtestCandles,
                ExpiryCandles: expiryCandles,
                MinimumSuccessRate: minimumSuccessRate);
            return Results.Ok(await svc.GetSignalAsync(
                asset, timeframe, options, autoExecute, ct, strategyId: strategyId));
        });
        return group;
    }
}

public static class BotEndpoints
{
    public static RouteGroupBuilder MapBotEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bot").WithTags("Bot").RequireAuthorization();
        group.MapGet("/status", (BotControlAppService svc) => Results.Ok(svc.Get()));
        group.MapPost("/start", async ([FromBody] BotStartRequest request, BotControlAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.StartAsync(request, ct)));
        group.MapPost("/pause", (BotControlAppService svc) => Results.Ok(svc.Pause()));
        group.MapPost("/stop", (BotControlAppService svc) => Results.Ok(svc.Stop()));
        group.MapPost("/apply", ([FromBody] BotApplyRequest request, BotControlAppService svc) => Results.Ok(svc.Apply(request)));
        return group;
    }
}

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/binolla/accounts")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/", async (
            AdminAppService svc,
            CancellationToken ct,
            [FromQuery] string? status = null,
            [FromQuery] string? q = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
            Results.Ok(await svc.ListAsync(status, ct, q, page, pageSize)));

        group.MapGet("/{id:guid}", async (Guid id, AdminAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct)));

        group.MapPost("/{id:guid}/approve", async (Guid id, AdminAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.ApproveAsync(id, ct)));

        group.MapPost("/{id:guid}/reject", async (Guid id, AdminAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.RejectAsync(id, ct)));

        var demo = app.MapGroup("/api/admin/demo-users")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        demo.MapGet("/", async (
            AdminAppService svc,
            CancellationToken ct,
            [FromQuery] string? active = "true",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
        {
            bool? activeFilter = active?.Trim().ToLowerInvariant() switch
            {
                "false" => false,
                "all" => null,
                _ => true,
            };
            return Results.Ok(await svc.ListMarketingDemoUsersAsync(ct, activeFilter, page, pageSize));
        });

        demo.MapPost("/", async (
            [FromBody] CreateMarketingDemoUserRequest request,
            AdminAppService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.CreateMarketingDemoUserAsync(request, ct)));

        demo.MapPatch("/{id:guid}", async (
            Guid id,
            [FromBody] SetMarketingDemoRequest request,
            AdminAppService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.SetMarketingDemoAsync(id, request, ct)));

        demo.MapPut("/{id:guid}/config", async (
            Guid id,
            [FromBody] UpdateMarketingDemoConfigRequest request,
            AdminAppService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.UpdateMarketingDemoConfigAsync(id, request, ct)));

        var users = app.MapGroup("/api/admin/users")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        users.MapGet("/", async (
            AdminAppService svc,
            CancellationToken ct,
            [FromQuery] string? q = null,
            [FromQuery] string? role = null,
            [FromQuery] bool? isMarketingDemo = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
            Results.Ok(await svc.ListUsersAsync(q, role, isMarketingDemo, page, pageSize, ct)));

        users.MapGet("/{id:guid}", async (Guid id, AdminAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetUserAsync(id, ct)));

        users.MapPatch("/{id:guid}", async (
            Guid id,
            [FromBody] PatchAdminUserRequest request,
            AdminAppService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.PatchUserAsync(id, request, ct)));

        var audit = app.MapGroup("/api/admin/audit")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        audit.MapGet("/", async (
            AdminAppService svc,
            CancellationToken ct,
            [FromQuery] Guid? userId = null,
            [FromQuery] string? action = null,
            [FromQuery] DateTimeOffset? from = null,
            [FromQuery] DateTimeOffset? to = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
            Results.Ok(await svc.ListAuditAsync(userId, action, from, to, page, pageSize, ct)));

        var notifications = app.MapGroup("/api/admin/notifications")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        notifications.MapGet("/", async (
            AdminAppService svc,
            CancellationToken ct,
            [FromQuery] Guid? userId = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
            Results.Ok(await svc.ListNotificationsAsync(userId, page, pageSize, ct)));

        notifications.MapPost("/", async (
            [FromBody] AdminSendNotificationRequest request,
            AdminAppService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.SendNotificationsAsync(request, ct)));

        var bots = app.MapGroup("/api/admin/bots")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        bots.MapGet("/", async (
            AdminAppService svc,
            CancellationToken ct,
            [FromQuery] string? state = null,
            [FromQuery] string? q = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
            Results.Ok(await svc.ListBotsAsync(state, q, page, pageSize, ct)));

        bots.MapGet("/{userId:guid}", async (Guid userId, AdminAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetBotAsync(userId, ct)));

        bots.MapPost("/{userId:guid}/control", async (
            Guid userId,
            [FromBody] AdminBotControlRequest request,
            AdminAppService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.ControlBotAsync(userId, request, ct)));

        var trades = app.MapGroup("/api/admin/trades")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        trades.MapGet("/", async (
            AdminAppService svc,
            CancellationToken ct,
            [FromQuery] Guid? userId = null,
            [FromQuery] string? status = null,
            [FromQuery] string? asset = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
            Results.Ok(await svc.ListTradesAsync(userId, status, asset, page, pageSize, ct)));

        return group;
    }
}

public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();

        group.MapGet("/", async (NotificationAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, NotificationAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct)));

        group.MapPost("/{id:guid}/read", async (Guid id, NotificationAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.MarkReadAsync(id, ct)));

        group.MapPost("/read-all", async (NotificationAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.MarkAllReadAsync(ct)));

        return group;
    }
}

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", async (
            ScarAlpha.Infrastructure.Persistence.AppDbContext db,
            IHostEnvironment env,
            IConfiguration config,
            CancellationToken ct) =>
        {
            try
            {
                var canConnect = await db.Database.CanConnectAsync(ct);
                var providerRaw = (config["DATABASE_PROVIDER"] ?? config["Database:Provider"] ?? "Npgsql").Trim();
                var efProvider = db.Database.ProviderName ?? "unknown";
                var isInMemory = efProvider.Contains("InMemory", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(providerRaw, "InMemory", StringComparison.OrdinalIgnoreCase);

                if (!canConnect)
                {
                    return Results.Json(new
                    {
                        status = "not_ready",
                        database = false,
                        environment = env.EnvironmentName,
                        databaseProvider = isInMemory ? "InMemory" : providerRaw,
                        efProvider
                    }, statusCode: 503);
                }

                return Results.Ok(new
                {
                    status = "ready",
                    database = true,
                    environment = env.EnvironmentName,
                    // Honest production signal: InMemory never survives restart.
                    databaseProvider = isInMemory ? "InMemory" : providerRaw,
                    efProvider,
                    persistent = !isInMemory,
                    note = "Binolla sessions are per-user and are not required for API readiness."
                });
            }
            catch
            {
                return Results.Json(new { status = "not_ready", database = false }, statusCode: 503);
            }
        });
    }
}
