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
using ScarAlpha.Infrastructure.BrokerGateway;
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
        if (DatabaseProviderHelper.IsInMemory(configuration))
        {
            var dbName = configuration["DATABASE_INMEMORY_NAME"] ?? "ScarAlphaLocal";
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        }
        else if (DatabaseProviderHelper.IsMySql(configuration))
        {
            var cs = configuration["DATABASE_CONNECTION_STRING"]
                     ?? configuration.GetConnectionString("Default")
                     ?? "Server=127.0.0.1;Port=3306;Database=scaralpha;User=root;Password=;";
            var serverVersion = ServerVersion.Parse("8.0.36-mysql");
            services.AddDbContext<AppDbContext>(o =>
                o.UseMySql(cs, serverVersion, mySql =>
                    mySql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));
        }
        else
        {
            var cs = configuration["DATABASE_CONNECTION_STRING"]
                     ?? configuration.GetConnectionString("Default")
                     ?? "Host=localhost;Port=5432;Database=scaralpha;Username=postgres;Password=postgres";
            services.AddDbContext<AppDbContext>(o =>
                o.UseNpgsql(cs, npgsql =>
                    npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));
        }

        services.AddHttpContextAccessor();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IBinollaLinkRepository, BinollaLinkRepository>();
        services.AddScoped<ITradeRepository, TradeRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IAppSettingRepository, AppSettingRepository>();
        services.AddScoped<INotificationWriter, NotificationWriter>();
        services.AddScoped<IReferralRepository, ReferralRepository>();
        services.AddScoped<IReferralQualificationService, ReferralQualificationService>();
        services.AddScoped<IReferralRewardService, ReferralRewardService>();
        services.AddScoped<IReferralAccrualService, ReferralAccrualService>();
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
        services.AddScoped<BinollaAuthAppService>();
        services.AddScoped<MeAppService>();
        services.AddScoped<AccountAppService>();
        services.AddScoped<StrategyAppService>();
        services.AddScoped<BinollaAppService>();
        services.AddScoped<BinollaGuidedLoginService>();
        services.AddScoped<MarketAppService>();
        services.AddScoped<RsiSignalAppService>();
        services.AddScoped<TradeAppService>();
        services.AddScoped<AdminAppService>();
        services.AddScoped<NotificationAppService>();
        services.AddScoped<BotControlAppService>();
        services.AddScoped<ReferralAppService>();
        services.AddScoped<AdminReferralAppService>();

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
        var calibLow = configuration.GetValue<decimal?>("Strategy:RsiCalibrationOffsetLow");
        if (calibLow is decimal calLo) Indicators.RsiCalibrationOffsetLow = calLo;

        // Dedicated market-data account. Unset = borrow a user's session (previous
        // behaviour); set = the whole fleet analyses through this one account.
        var analysisUser = configuration["Strategy:AnalysisUserId"];
        if (Guid.TryParse(analysisUser, out var analysisUserId))
            AnalysisAccount.UserId = analysisUserId;

        // How long a pair sits out after the bot loses on it. 0 disables the rule.
        var pairCooldown = configuration.GetValue<int?>("Strategy:PairLossCooldownSeconds");
        if (pairCooldown is int pc) PairCooldownRegistry.CooldownSeconds = pc;

        var minPairPayout = configuration.GetValue<int?>("Strategy:MinPairPayoutPercent");
        if (minPairPayout is int mp) PairPayoutGate.MinPayoutPercent = mp;

        // Referral program — tunable without a rebuild (see appsettings "Referral" section).
        ReferralConfig.MinDepositUsd = configuration.GetValue("Referral:MinDepositUsd", ReferralConfig.MinDepositUsd);
        ReferralConfig.QualificationDays = configuration.GetValue("Referral:QualificationDays", ReferralConfig.QualificationDays);
        ReferralConfig.MinActiveDaysInWindow = configuration.GetValue("Referral:MinActiveDaysInWindow", ReferralConfig.MinActiveDaysInWindow);
        ReferralConfig.BalancePollMinutes = configuration.GetValue("Referral:BalancePollMinutes", ReferralConfig.BalancePollMinutes);
        ReferralConfig.CommissionFromQualifiedOnly = configuration.GetValue("Referral:CommissionFromQualifiedOnly", ReferralConfig.CommissionFromQualifiedOnly);
        ReferralConfig.MinPayoutUsd = configuration.GetValue("Referral:MinPayoutUsd", ReferralConfig.MinPayoutUsd);
        ReferralConfig.WebBaseUrl = configuration["Referral:WebBaseUrl"] ?? ReferralConfig.WebBaseUrl;

        services.AddSingleton<IRsiSignalService, RsiSignalService>();
        services.AddSingleton<IEmaRsiSignalService, EmaRsiSignalService>();
        services.AddSingleton<IAlternatingSignalService, AlternatingSignalService>();
        // Shared across all users: one analysis per pair per closed bar.
        services.AddSingleton<MarketAnalysisCache>();
        // One trading decision per strategy+duration per bar, fanned out to every user
        // in that cohort — see CohortSignalCache.
        services.AddSingleton<CohortSignalCache>();
        // Global stop switch — read on every worker tick, so it is a singleton
        // holding an in-memory copy of the persisted flag.
        services.AddSingleton<IBotMaintenanceService>(sp =>
            new BotMaintenanceService(
                new ScopedAppSettingRepository(sp.GetRequiredService<IServiceScopeFactory>())));
        services.AddSingleton<EmaRsiTradeTracker>();

        // The whole scanned pair set must stay cached, or every bar re-fetches it.
        var historyEntries = configuration.GetValue<int?>("Binolla:MaxHistoricalEntries");
        if (historyEntries is int he) BinollaSessionState.MaxHistoricalEntries = he;

        var binollaOptions = new BinollaSessionManagerOptions
        {
            EnableAutoReconnect = configuration.GetValue("Binolla:EnableAutoReconnect", true),
            MaxConcurrentSessions = configuration.GetValue("Binolla:MaxConcurrentSessions", 5000),
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

        // Broker-neutral routing. Binolla keeps its own C# session manager underneath;
        // every other venue is served by the Python gateway in backend/brokers.
        var gatewayUrl = configuration["BROKER_GATEWAY_URL"]
                         ?? configuration["Brokers:GatewayUrl"]
                         ?? BrokerGatewayDefaults.DefaultBaseUrl;
        var gatewayToken = configuration["BROKER_GATEWAY_TOKEN"] ?? string.Empty;

        services.AddHttpClient(BrokerGatewayDefaults.HttpClientName, http =>
        {
            http.BaseAddress = new Uri(gatewayUrl);
            // No single ceiling fits both kinds of call: a quote must not hang a worker
            // tick, while waiting for a 15-minute expiry to close legitimately takes
            // longer than any sane socket timeout. BrokerGatewayClient therefore gives
            // every request its own deadline, and this one is disabled rather than
            // silently cutting a long outcome wait short.
            http.Timeout = Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrWhiteSpace(gatewayToken))
                http.DefaultRequestHeaders.Add("X-Gateway-Token", gatewayToken);
        });

        services.AddSingleton<IBrokerSessionManager, BrokerSessionManager>();

        // Singleton so the venue lookup is one cached read on the hot signal path rather
        // than a query per pair per scan.
        services.AddSingleton<IBrokerResolver, BrokerResolver>();

        // Guided login (user solves the CAPTCHA themselves) runs in its own Node process,
        // because it holds a browser open per login and the one-shot capture cannot.
        var interactiveUrl = configuration["BINOLLA_INTERACTIVE_URL"]
                             ?? NodeBinollaInteractiveAuth.DefaultBaseUrl;
        var interactiveToken = configuration["BINOLLA_INTERACTIVE_TOKEN"] ?? string.Empty;

        services.AddHttpClient(NodeBinollaInteractiveAuth.HttpClientName, http =>
        {
            http.BaseAddress = new Uri(interactiveUrl);
            // Each call sets its own deadline: starting a browser is slow, relaying a
            // click must not be.
            http.Timeout = Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrWhiteSpace(interactiveToken))
                http.DefaultRequestHeaders.Add("X-Auth-Token", interactiveToken);
        });

        services.AddSingleton<IBinollaInteractiveAuth, NodeBinollaInteractiveAuth>();

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
        // Holds the pair history resident so the bar-close scan reads RAM instead of
        // re-fetching serially from the socket — see MarketWarmupWorker.
        services.AddHostedService<MarketWarmupWorker>();
        // Re-checks settled trades against Binolla and corrects the ones that disagree.
        services.AddHostedService<TradeReconciliationWorker>();
        // Backstop for the referral deposit signal — see ReferralBalanceWatcher.
        services.AddHostedService<ReferralBalanceWatcher>();

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
