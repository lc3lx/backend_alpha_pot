using Microsoft.AspNetCore.Mvc;
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
        return group;
    }
}

public static class StrategyEndpoints
{
    public static RouteGroupBuilder MapStrategyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/strategies").WithTags("Strategies").RequireAuthorization();
        group.MapGet("/", (StrategyAppService svc) => Results.Ok(svc.ListStrategies()));
        group.MapGet("/rsi/signal/{asset}", async (
            string asset,
            RsiSignalAppService svc,
            CancellationToken ct,
            [FromQuery] int period = 60) =>
        {
            return Results.Ok(await svc.GetSignalAsync(asset, period, ct));
        });
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
            [FromQuery] string? status = null) =>
            Results.Ok(await svc.ListAsync(status, ct)));

        group.MapGet("/{id:guid}", async (Guid id, AdminAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct)));

        group.MapPost("/{id:guid}/approve", async (Guid id, AdminAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.ApproveAsync(id, ct)));

        group.MapPost("/{id:guid}/reject", async (Guid id, AdminAppService svc, CancellationToken ct) =>
            Results.Ok(await svc.RejectAsync(id, ct)));

        return group;
    }
}

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", async (ScarAlpha.Infrastructure.Persistence.AppDbContext db, CancellationToken ct) =>
        {
            try
            {
                var canConnect = await db.Database.CanConnectAsync(ct);
                if (!canConnect)
                    return Results.Json(new { status = "not_ready", database = false }, statusCode: 503);

                return Results.Ok(new
                {
                    status = "ready",
                    database = true,
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
