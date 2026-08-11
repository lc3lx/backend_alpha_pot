using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ScarAlpha.Api.Endpoints;
using ScarAlpha.Api.Middleware;
using ScarAlpha.Application.Common;
using ScarAlpha.Infrastructure;
using ScarAlpha.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    builder.Services.AddScarAlphaInfrastructure(builder.Configuration);
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var corsOrigins = builder.Configuration["CORS_ORIGINS"]
                      ?? builder.Configuration["Cors:Origins"]
                      ?? "http://localhost:5173";

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AppCors", policy =>
        {
            policy.WithOrigins(corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
    });

    var tradePermitLimit = builder.Configuration.GetValue("RateLimiting:Trades:PermitLimit", 30);
    var tradeWindowSeconds = builder.Configuration.GetValue("RateLimiting:Trades:WindowSeconds", 60);
    var authPermitLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 20);
    var authWindowSeconds = builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60);
    var connectPermitLimit = builder.Configuration.GetValue("RateLimiting:Connect:PermitLimit", 10);
    var connectWindowSeconds = builder.Configuration.GetValue("RateLimiting:Connect:WindowSeconds", 60);

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, token) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsJsonAsync(
                new { code = ApiErrorCodes.RateLimited, message = "Too many requests. Try again later." },
                token);
            Log.Information("Rate limited request from {RemoteIp} path={Path}",
                context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                context.HttpContext.Request.Path.Value);
        };
        options.AddPolicy("trades", httpContext =>
        {
            var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            var permit = config.GetValue("RateLimiting:Trades:PermitLimit", tradePermitLimit);
            var windowSec = config.GetValue("RateLimiting:Trades:WindowSeconds", tradeWindowSeconds);
            var partitionKey =
                httpContext.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User?.FindFirst("sub")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anon";

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permit,
                    Window = TimeSpan.FromSeconds(windowSec),
                    QueueLimit = 0
                });
        });
        options.AddPolicy("auth", httpContext =>
        {
            var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            var permit = config.GetValue("RateLimiting:Auth:PermitLimit", authPermitLimit);
            var windowSec = config.GetValue("RateLimiting:Auth:WindowSeconds", authWindowSeconds);
            var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permit,
                    Window = TimeSpan.FromSeconds(windowSec),
                    QueueLimit = 0
                });
        });
        options.AddPolicy("connect", httpContext =>
        {
            var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            var permit = config.GetValue("RateLimiting:Connect:PermitLimit", connectPermitLimit);
            var windowSec = config.GetValue("RateLimiting:Connect:WindowSeconds", connectWindowSeconds);
            var partitionKey =
                httpContext.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User?.FindFirst("sub")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anon";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permit,
                    Window = TimeSpan.FromSeconds(windowSec),
                    QueueLimit = 0
                });
        });
    });

    var app = builder.Build();

    ValidateProductionSecrets(app);

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }
    }

    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseSerilogRequestLogging(opts =>
    {
        opts.GetLevel = (httpContext, elapsed, ex) =>
            ex is not null ? LogEventLevel.Error :
            httpContext.Response.StatusCode >= 500 ? LogEventLevel.Error :
            LogEventLevel.Information;
    });

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors("AppCors");
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapHealthEndpoints();
    app.MapAuthEndpoints().RequireRateLimiting("auth");
    app.MapMeEndpoints();
    app.MapBinollaEndpoints();
    app.MapMarketEndpoints();
    app.MapAccountEndpoints();
    app.MapStrategyEndpoints();
    app.MapAdminEndpoints();
    app.MapTradeEndpoints().RequireRateLimiting("trades");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static void ValidateProductionSecrets(WebApplication app)
{
    if (!app.Environment.IsProduction())
        return;

    var config = app.Configuration;
    var jwt = config["JWT_SECRET"] ?? config["Jwt:Secret"] ?? string.Empty;
    var enc = config["BINOLLA_TOKEN_ENCRYPTION_KEY"] ?? config["Security:BinollaTokenEncryptionKey"] ?? string.Empty;
    var bot = config["TELEGRAM_BOT_TOKEN"] ?? config["Telegram:BotToken"] ?? string.Empty;

    static bool IsWeak(string value, params string[] markers) =>
        string.IsNullOrWhiteSpace(value)
        || markers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));

    if (IsWeak(jwt, "dev-only", "change-me")
        || IsWeak(enc, "dev-binolla", "change-me")
        || IsWeak(bot, "REPLACE_ME", "000000000"))
    {
        throw new InvalidOperationException(
            "Production refuses weak/default JWT, encryption, or Telegram secrets. Configure env overrides.");
    }
}

public partial class Program;
