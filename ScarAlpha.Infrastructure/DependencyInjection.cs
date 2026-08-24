using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Services;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Session;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Access;
using ScarAlpha.Infrastructure.Auth;
using ScarAlpha.Infrastructure.Binolla;
using ScarAlpha.Infrastructure.Persistence;
using ScarAlpha.Infrastructure.Security;
using ScarAlpha.Infrastructure.Strategies;
using ScarAlpha.Infrastructure.Telegram;
using ScarAlpha.Infrastructure.Notifications;
using ScarAlpha.Infrastructure.Workers;

namespace ScarAlpha.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddScarAlphaInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = (configuration["DATABASE_PROVIDER"] ?? configuration["Database:Provider"] ?? "Npgsql")
            .Trim();

        if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            var dbName = configuration["DATABASE_INMEMORY_NAME"] ?? "ScarAlphaLocal";
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        }
        else
        {
            var cs = configuration["DATABASE_CONNECTION_STRING"]
                     ?? configuration.GetConnectionString("Default")
                     ?? "Host=localhost;Port=5432;Database=scaralpha;Username=postgres;Password=postgres";
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs));
        }

        services.AddHttpContextAccessor();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IBinollaLinkRepository, BinollaLinkRepository>();
        services.AddScoped<ITradeRepository, TradeRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationWriter, NotificationWriter>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddSingleton<ITelegramAuthService, TelegramAuthService>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IUserPasswordHasher, UserPasswordHasher>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddSingleton<IIdempotencyGate, IdempotencyGate>();
        services.AddSingleton<IStrategyRegistry, StrategyRegistry>();
        services.AddScoped<IBotAccessService, BotAccessService>();
        services.AddSingleton<IBotRuntimeService, BotRuntimeService>();
        services.AddHostedService<BotRuntimeRestoreHostedService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IMarketingDemoService, MarketingDemoService>();

        services.AddScoped<AuthAppService>();
        services.AddScoped<MeAppService>();
        services.AddScoped<AccountAppService>();
        services.AddScoped<StrategyAppService>();
        services.AddScoped<BinollaAppService>();
        services.AddScoped<MarketAppService>();
        services.AddScoped<RsiSignalAppService>();
        services.AddScoped<TradeAppService>();
        services.AddScoped<AdminAppService>();
        services.AddScoped<NotificationAppService>();
        services.AddScoped<BotControlAppService>();

        services.AddSingleton<IBinollaCredentialAuth, NodeBinollaCredentialAuth>();

        services.AddSingleton<IRsiCalculator, RsiCalculator>();
        // Wilder RSI is recursive: too little history and the value drifts away from the
        // broker chart. Default 150 bars; lower only if Binolla pushes shallow history.
        var warmup = configuration.GetValue<int?>("Strategy:MinRsiWarmupCandles");
        if (warmup is int w)
            IndicatorWarmup.MinRsiCandles = w;

        // Pine useTrend. Turn off only if Binolla does not serve ~200 bars of 15m history.
        var emaTrend = configuration.GetValue<bool?>("Strategy:EmaUseTrendFilter");
        if (emaTrend is bool t)
            RsiSignalAppService.EmaTrendFilterEnabled = t;

        // Regime filtering. Off keeps the pre-regime behaviour exactly.
        StrategyGate.RegimeEnabled = configuration.GetValue("Strategy:Regime:Enabled", false);

        // Entry levels are tunable so they can be rolled back without a rebuild.
        var callMax = configuration.GetValue<decimal?>("Strategy:RsiCallMax");
        if (callMax is decimal cm) RsiEntryLevels.CallMax = cm;
        var putMin = configuration.GetValue<decimal?>("Strategy:RsiPutMin");
        if (putMin is decimal pm) RsiEntryLevels.PutMin = pm;
        var minVisits = configuration.GetValue<int?>("Strategy:MinZoneVisits");
        if (minVisits is int mv) RsiEntryLevels.MinZoneVisits = mv;
        var ttl = configuration.GetValue<int?>("Strategy:SetupTtlSeconds");
        if (ttl is int t2) RsiEntryLevels.SetupTtlSeconds = t2;
        var calib = configuration.GetValue<decimal?>("Strategy:RsiCalibrationOffset");
        if (calib is decimal cal) Indicators.RsiCalibrationOffset = cal;

        services.AddSingleton<IRsiSignalService, RsiSignalService>();
        services.AddSingleton<IEmaRsiSignalService, EmaRsiSignalService>();
        services.AddSingleton<IAlternatingSignalService, AlternatingSignalService>();
        // Shared across all users: one analysis per pair per closed bar.
        services.AddSingleton<MarketAnalysisCache>();
        services.AddSingleton<EmaRsiTradeTracker>();

        var binollaOptions = new BinollaSessionManagerOptions
        {
            EnableAutoReconnect = configuration.GetValue("Binolla:EnableAutoReconnect", true),
            MaxConcurrentSessions = configuration.GetValue("Binolla:MaxConcurrentSessions", 100),
            EnableChartConnection = false,
            // Fresh Binolla auth often needs >20s; login capture already succeeded in PM2 logs.
            AuthenticationTimeout = TimeSpan.FromSeconds(
                configuration.GetValue("Binolla:AuthenticationTimeoutSeconds", 45)),
            MarketDataTimeout = TimeSpan.FromSeconds(
                configuration.GetValue("Binolla:MarketDataTimeoutSeconds", 30)),
            MarketHttpWait = TimeSpan.FromSeconds(
                configuration.GetValue("Binolla:MarketHttpWaitSeconds", 4))
        };
        services.AddSingleton(binollaOptions);
        services.AddSingleton<IBinollaSessionManager>(sp =>
            new BinollaSessionManager(sp.GetRequiredService<BinollaSessionManagerOptions>()));

        services.Configure<BinollaSessionRestoreOptions>(options =>
        {
            options.Enabled = configuration.GetValue("Binolla:SessionRestore:Enabled", true);
            options.MaxDegreeOfParallelism = configuration.GetValue("Binolla:SessionRestore:MaxDegreeOfParallelism", 3);
            options.MaxAttempts = configuration.GetValue("Binolla:SessionRestore:MaxAttempts", 5);
            options.InitialDelayMs = configuration.GetValue("Binolla:SessionRestore:InitialDelayMs", 500);
            options.MaxDelayMs = configuration.GetValue("Binolla:SessionRestore:MaxDelayMs", 30_000);
            options.LazyMaxAttempts = configuration.GetValue("Binolla:SessionRestore:LazyMaxAttempts", 1);
            options.FailureCooldownSeconds = configuration.GetValue("Binolla:SessionRestore:FailureCooldownSeconds", 30);
        });
        services.AddSingleton<BinollaSessionRestoreService>();
        services.AddSingleton<IBinollaSessionRestorer>(sp => sp.GetRequiredService<BinollaSessionRestoreService>());
        // Register restore hosted service before trade outcome so StartAsync order favors restore kickoff first.
        services.AddHostedService(sp => sp.GetRequiredService<BinollaSessionRestoreService>());

        services.AddSingleton<TradeOutcomeWorker>();
        services.AddSingleton<ITradeOutcomeWorker>(sp => sp.GetRequiredService<TradeOutcomeWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<TradeOutcomeWorker>());

        services.AddSingleton<BotSignalWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<BotSignalWorker>());

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((options, configuration) =>
            {
                var jwtSecret = configuration["JWT_SECRET"] ?? configuration["Jwt:Secret"];
                if (string.IsNullOrWhiteSpace(jwtSecret))
                    throw new InvalidOperationException("JWT_SECRET is required.");
                var issuer = configuration["JWT_ISSUER"] ?? configuration["Jwt:Issuer"] ?? "ScarAlpha";
                var audience = configuration["JWT_AUDIENCE"] ?? configuration["Jwt:Audience"] ?? "ScarAlpha.App";

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy =>
                policy.RequireRole(nameof(UserRole.Admin)));
        });
        return services;
    }
}
