using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;
using EngineDirection = ScarAlpha.Binolla.Models.TradeDirection;
using DomainDirection = ScarAlpha.Domain.Enums.TradeDirection;
using ScarAlpha.Infrastructure.Persistence;
using ScarAlpha.Infrastructure.Workers;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class RateLimitedApiFactory : WebApplicationFactory<Program>
{
    public Mock<IBinollaSessionManager> SessionManager { get; } = new(MockBehavior.Loose);
    public Mock<IBinollaClient> Client { get; } = new(MockBehavior.Loose);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TELEGRAM_BOT_TOKEN"] = ApiFactory.BotToken,
                ["Telegram:BotToken"] = ApiFactory.BotToken,
                ["JWT_SECRET"] = ApiFactory.JwtSecret,
                ["Jwt:Secret"] = ApiFactory.JwtSecret,
                ["JWT_ISSUER"] = "ScarAlpha",
                ["JWT_AUDIENCE"] = "ScarAlpha.App",
                ["BINOLLA_TOKEN_ENCRYPTION_KEY"] = ApiFactory.EncryptionKey,
                ["Security:BinollaTokenEncryptionKey"] = ApiFactory.EncryptionKey,
                ["RateLimiting:Trades:PermitLimit"] = "2",
                ["RateLimiting:Trades:WindowSeconds"] = "60"
            });
        });

        builder.ConfigureServices(services =>
        {
            var dbName = "ScarAlphaRateLimit_" + Guid.NewGuid().ToString("N");
            foreach (var d in services.Where(x =>
                         x.ServiceType == typeof(AppDbContext) ||
                         x.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                         (x.ServiceType.IsGenericType &&
                          x.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration", StringComparison.Ordinal))).ToList())
                services.Remove(d);

            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.RemoveAll<IBinollaSessionManager>();

            // Never run real Playwright Binolla login in tests.
            services.RemoveAll<IBinollaCredentialAuth>();
            var credAuth = new Mock<IBinollaCredentialAuth>();
            credAuth.Setup(c => c.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("""42["authorization",{"isDemo":true,"token":"cred-login-token-abcdef"}]""");
            credAuth.Setup(c => c.SignUpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("""42["authorization",{"isDemo":true,"token":"cred-signup-token-abcdef"}]""");
            services.AddSingleton(credAuth.Object);

            Client.SetupGet(c => c.Lifecycle).Returns(SessionLifecycleState.Connected);
            Client.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Client.Setup(c => c.ChangeAccountAsync(It.IsAny<AccountType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Client.Setup(c => c.GetBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new BalanceInfo
            {
                DemoBalance = 1000m, RealBalance = 0m, CurrentType = AccountType.Demo, LastUpdated = DateTimeOffset.UtcNow
            });
            Client.Setup(c => c.PlaceOrderAsync(It.IsAny<string>(), It.IsAny<EngineDirection>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OrderResponse
                {
                    OrderId = "rl-" + Guid.NewGuid().ToString("N")[..6],
                    Asset = "EURUSD_otc",
                    Direction = EngineDirection.Call,
                    Amount = 1,
                    ExpiryTime = DateTimeOffset.UtcNow.AddSeconds(60),
                    PlacedAt = DateTimeOffset.UtcNow,
                    Status = OrderStatus.Open,
                    BalanceType = AccountType.Demo,
                    RequestId = 1
                });
            Client.Setup(c => c.WaitOutcomeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string id, CancellationToken ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return new TradeOutcome { OrderId = id, ProfitLoss = 0, Result = TradeResult.Tie };
                });

            var connectedUsers = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            SessionManager.Setup(m => m.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string userId, string _, CancellationToken _) =>
                {
                    connectedUsers[userId] = 1;
                    return Client.Object;
                });
            SessionManager.Setup(m => m.Get(It.IsAny<string>()))
                .Returns((string userId) => connectedUsers.ContainsKey(userId) ? Client.Object : null);
            SessionManager.Setup(m => m.DisconnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string userId, CancellationToken ct) =>
                {
                    connectedUsers.TryRemove(userId, out var _);
                    return Task.CompletedTask;
                });
            SessionManager.Setup(m => m.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            SessionManager.SetupGet(m => m.ActiveSessionCount).Returns(() => connectedUsers.Count);
            SessionManager.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);
            services.AddSingleton(SessionManager.Object);
        });
    }
}

public sealed class Phase3MarketApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public Phase3MarketApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Assets_quote_candles_work_when_connected()
    {
        var token = await LoginAndConnectAsync(7001);

        var assets = await AuthedGetAsync(token, "/api/market/assets");
        assets.EnsureSuccessStatusCode();
        var assetsJson = await assets.Content.ReadFromJsonAsync<JsonElement>();
        assetsJson.GetProperty("assets").GetArrayLength().Should().BeGreaterThan(0);
        assetsJson.GetProperty("assets")[0].TryGetProperty("payout", out _).Should().BeTrue();
        (await assets.Content.ReadAsStringAsync()).Should().NotContain("ssid");

        var price = await AuthedGetAsync(token, "/api/market/price/EURUSD_otc");
        price.EnsureSuccessStatusCode();
        var priceJson = await price.Content.ReadFromJsonAsync<JsonElement>();
        priceJson.GetProperty("price").GetDecimal().Should().BeGreaterThan(0);

        var candles = await AuthedGetAsync(token, "/api/market/candles/EURUSD_otc?period=60");
        candles.EnsureSuccessStatusCode();
        var candlesJson = await candles.Content.ReadFromJsonAsync<JsonElement>();
        candlesJson.GetProperty("candles").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Market_endpoints_require_connected_session()
    {
        var token = await LoginAsync(7002);
        var res = await AuthedGetAsync(token, "/api/market/assets");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("BINOLLA_NOT_CONNECTED");
    }

    [Fact]
    public async Task Expired_session_returns_controlled_error_on_market()
    {
        var token = await LoginAndConnectAsync(7003);
        _factory.Client.Setup(c => c.GetTradingAssetsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BinollaAuthenticationException("expired"));

        var res = await AuthedGetAsync(token, "/api/market/assets");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().Contain("BINOLLA_SESSION_EXPIRED");

        // restore for other tests sharing fixture
        _factory.Client.Setup(c => c.GetTradingAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TradingAsset>
            {
                new() { Symbol = "EURUSD_otc", Description = "EUR/USD OTC", IsOpen = true, PayoutPercentage = 85 }
            });
    }

    [Fact]
    public async Task Health_and_ready_exist()
    {
        (await _client.GetAsync("/health")).EnsureSuccessStatusCode();
        var ready = await _client.GetAsync("/health/ready");
        ready.EnsureSuccessStatusCode();
        (await ready.Content.ReadAsStringAsync()).Should().Contain("ready");
    }

    private async Task<string> LoginAndConnectAsync(long id)
    {
        var token = await LoginAsync(id);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/connect")
        {
            Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);
        return token;
    }

    private async Task<string> LoginAsync(long telegramId)
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, telegramId);
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private async Task<HttpResponseMessage> AuthedGetAsync(string token, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(req);
    }
}

public sealed class Phase3TradingHardeningTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public Phase3TradingHardeningTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Concurrent_duplicate_idempotency_creates_exactly_one_order()
    {
        var token = await LoginAndConnectAsync(7101);
        var placeCount = 0;
        _factory.Client.Setup(c => c.PlaceOrderAsync(
                It.IsAny<string>(), It.IsAny<EngineDirection>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string asset, EngineDirection dir, decimal amount, int dur, CancellationToken _) =>
            {
                Interlocked.Increment(ref placeCount);
                return new OrderResponse
                {
                    OrderId = "idem-" + Guid.NewGuid().ToString("N")[..8],
                    Asset = asset,
                    Direction = dir,
                    Amount = amount,
                    ExpiryTime = DateTimeOffset.UtcNow.AddSeconds(dur),
                    PlacedAt = DateTimeOffset.UtcNow,
                    Status = OrderStatus.Open,
                    BalanceType = AccountType.Demo,
                    RequestId = 1
                };
            });

        var payload = new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 60 };
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Idempotency-Key", "concurrent-ABC123");
            var res = await _client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync<JsonElement>();
        });

        var results = await Task.WhenAll(tasks);
        var ids = results.Select(r => r.GetProperty("id").GetString()).Distinct().ToList();
        ids.Should().HaveCount(1);
        placeCount.Should().Be(1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.TelegramUserId == 7101);
        (await db.Trades.CountAsync(t => t.UserId == user.Id && t.IdempotencyKey == "concurrent-ABC123")).Should().Be(1);
    }

    [Fact]
    public async Task Demo_trade_accepted_and_outcome_updates_trade()
    {
        var token = await LoginAndConnectAsync(7102);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
        {
            Content = JsonContent.Create(new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 60 })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Idempotency-Key", "outcome-1");
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var trade = await res.Content.ReadFromJsonAsync<JsonElement>();
        var tradeId = Guid.Parse(trade.GetProperty("id").GetString()!);
        trade.GetProperty("status").GetString().Should().BeOneOf("Running", "Pending", "Profit");

        // Wait for background outcome worker
        Trade? updated = null;
        for (var i = 0; i < 40; i++)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            updated = await db.Trades.SingleAsync(t => t.Id == tradeId);
            if (updated.Status is TradeStatus.Profit or TradeStatus.Loss or TradeStatus.Tie)
                break;
            await Task.Delay(50);
        }

        updated!.Status.Should().Be(TradeStatus.Profit);
        updated.Pnl.Should().Be(0.8m);
    }

    [Fact]
    public void Duplicate_outcome_does_not_corrupt_terminal_trade()
    {
        var status = TradeStatus.Profit;
        TradeStateMachine.TryTransition(ref status, TradeStatus.Loss).Should().BeFalse();
        status.Should().Be(TradeStatus.Profit);

        status = TradeStatus.Running;
        TradeStateMachine.TryTransition(ref status, TradeStatus.Profit).Should().BeTrue();
        TradeStateMachine.TryTransition(ref status, TradeStatus.Loss).Should().BeFalse();
    }

    [Fact]
    public async Task Two_users_concurrent_isolation_for_market_balance_and_trades()
    {
        var tokenA = await LoginAndConnectAsync(7201);
        var tokenB = await LoginAndConnectAsync(7202);

        async Task<(string body, HttpStatusCode code)> Call(string token, HttpMethod method, string url, object? body = null, string? idem = null)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (idem is not null) req.Headers.Add("Idempotency-Key", idem);
            if (body is not null) req.Content = JsonContent.Create(body);
            var res = await _client.SendAsync(req);
            return (await res.Content.ReadAsStringAsync(), res.StatusCode);
        }

        var tasks = new[]
        {
            Call(tokenA, HttpMethod.Get, "/api/market/assets"),
            Call(tokenB, HttpMethod.Get, "/api/market/assets"),
            Call(tokenA, HttpMethod.Get, "/api/market/price/EURUSD_otc"),
            Call(tokenB, HttpMethod.Get, "/api/market/price/GBPUSD_otc"),
            Call(tokenA, HttpMethod.Get, "/api/binolla/balance"),
            Call(tokenB, HttpMethod.Get, "/api/binolla/balance"),
            Call(tokenA, HttpMethod.Post, "/api/trades", new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 60 }, "iso-a"),
            Call(tokenB, HttpMethod.Post, "/api/trades", new { asset = "GBPUSD_otc", direction = "PUT", amount = 1, durationSeconds = 60 }, "iso-b"),
        };

        var results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(r => r.code == HttpStatusCode.OK);
        results.Should().OnlyContain(r => !r.body.Contains("ssid", StringComparison.OrdinalIgnoreCase));

        var tradeA = JsonDocument.Parse(results[6].body).RootElement.GetProperty("id").GetString();
        var steal = await Call(tokenB, HttpMethod.Get, $"/api/trades/{tradeA}");
        steal.code.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Server_restart_recovery_marks_running_unknown_without_session()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            TelegramUserId = 7301,
            Username = "recover",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            BinollaOrderId = "orphan-order-1",
            Asset = "EURUSD_otc",
            Direction = DomainDirection.Call,
            Amount = 1,
            DurationSeconds = 60,
            Status = TradeStatus.Running,
            IdempotencyKey = "recover-1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Trades.Add(trade);
        await db.SaveChangesAsync();

        var sessions = new Mock<IBinollaSessionManager>(MockBehavior.Loose);
        sessions.Setup(s => s.Get(It.IsAny<string>())).Returns((IBinollaClient?)null);
        var restorer = new Mock<IBinollaSessionRestorer>(MockBehavior.Loose);
        restorer.SetupGet(r => r.WhenInitialRestoreCompleted).Returns(Task.CompletedTask);
        var worker = new TradeOutcomeWorker(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            sessions.Object,
            restorer.Object,
            NullLogger<TradeOutcomeWorker>.Instance);

        await worker.RecoverOpenTradesAsync(CancellationToken.None);

        await db.Entry(trade).ReloadAsync();
        trade.Status.Should().Be(TradeStatus.Unknown);
        trade.ErrorCode.Should().Be("RECOVERY_NO_SESSION");
    }

    private async Task<string> LoginAndConnectAsync(long id)
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, id);
        var login = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/connect")
        {
            Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);
        return token;
    }
}

public sealed class Phase3RateLimitTests : IClassFixture<RateLimitedApiFactory>
{
    private readonly RateLimitedApiFactory _factory;
    private readonly HttpClient _client;

    public Phase3RateLimitTests(RateLimitedApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Trade_burst_is_rate_limited_server_side()
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, 7401);
        var login = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

        using (var connect = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/connect")
        {
            Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" })
        })
        {
            connect.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await _client.SendAsync(connect)).EnsureSuccessStatusCode();
        }
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);

        HttpStatusCode? last = null;
        for (var i = 0; i < 5; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
            {
                Content = JsonContent.Create(new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 60 })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Idempotency-Key", $"rl-{i}");
            last = (await _client.SendAsync(req)).StatusCode;
            if (last == HttpStatusCode.TooManyRequests)
                break;
        }

        last.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
