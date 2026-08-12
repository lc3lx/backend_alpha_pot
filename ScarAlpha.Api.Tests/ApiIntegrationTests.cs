using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ConfigurationBuilder = Microsoft.Extensions.Configuration.ConfigurationBuilder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Infrastructure.Persistence;
using ScarAlpha.Infrastructure.Security;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string BotToken = "123456:TEST_BOT_TOKEN_FOR_UNIT_TESTS";
    public const string JwtSecret = "test-jwt-secret-key-32-characters!!";
    public const string EncryptionKey = "test-binolla-encryption-key-32ch";

    public Mock<IBinollaSessionManager> SessionManager { get; } = new(MockBehavior.Strict);
    public Mock<IBinollaClient> Client { get; } = new(MockBehavior.Loose);
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> ConnectedUsers { get; } =
        new(StringComparer.Ordinal);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TELEGRAM_BOT_TOKEN"] = BotToken,
                ["Telegram:BotToken"] = BotToken,
                ["JWT_SECRET"] = JwtSecret,
                ["Jwt:Secret"] = JwtSecret,
                ["JWT_ISSUER"] = "ScarAlpha",
                ["JWT_AUDIENCE"] = "ScarAlpha.App",
                ["BINOLLA_TOKEN_ENCRYPTION_KEY"] = EncryptionKey,
                ["Security:BinollaTokenEncryptionKey"] = EncryptionKey,
                ["Cors:Origins"] = "http://localhost:5173",
                ["RateLimiting:Trades:PermitLimit"] = "30",
                ["RateLimiting:Trades:WindowSeconds"] = "60",
                ["Admin:TelegramUserIds"] = "999001",
                ["Binolla:SessionRestore:InitialDelayMs"] = "10",
                ["Binolla:SessionRestore:MaxDelayMs"] = "50",
                ["Binolla:SessionRestore:MaxAttempts"] = "3"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace DB with a stable InMemory database for this factory instance.
            // Guid must be captured once â€” evaluating NewGuid() inside the options lambda
            // would create a new empty database per DbContext scope.
            var dbName = "ScarAlphaApiTests_" + Guid.NewGuid().ToString("N");

            var toRemove = services.Where(d =>
                d.ServiceType == typeof(AppDbContext) ||
                d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                (d.ServiceType.IsGenericType &&
                 d.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration", StringComparison.Ordinal))).ToList();
            foreach (var d in toRemove)
                services.Remove(d);

            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));

            // Replace Binolla session manager with mock
            services.RemoveAll<IBinollaSessionManager>();
            services.RemoveAll<BinollaSessionManagerOptions>();

            Client.SetupGet(c => c.UserId).Returns("mock");
            Client.SetupGet(c => c.Lifecycle).Returns(SessionLifecycleState.Connected);
            Client.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .Returns(Task.CompletedTask);
            Client.Setup(c => c.ChangeAccountAsync(It.IsAny<AccountType>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Client.Setup(c => c.GetBalanceAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BalanceInfo
                {
                    DemoBalance = 1000m,
                    RealBalance = 0m,
                    CurrentType = AccountType.Demo,
                    LastUpdated = DateTimeOffset.UtcNow
                });
            Client.Setup(c => c.DisconnectAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Client.Setup(c => c.GetTradingAssetsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TradingAsset>
                {
                    new() { Symbol = "EURUSD_otc", Description = "EUR/USD OTC", IsOpen = true, PayoutPercentage = 85, Category = "currency" },
                    new() { Symbol = "GBPUSD_otc", Description = "GBP/USD OTC", IsOpen = true, PayoutPercentage = 80, Category = "currency" }
                });
            Client.Setup(c => c.SubscribePairAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Client.Setup(c => c.GetLatestQuoteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string asset, CancellationToken _) => new QuoteData
                {
                    Pair = asset,
                    Price = 1.23456,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    ReceivedAt = DateTimeOffset.UtcNow
                });
            Client.Setup(c => c.GetHistoryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string asset, int period, CancellationToken _) => new HistoryData
                {
                    Asset = asset,
                    Period = period,
                    Candles =
                    [
                        new CandlestickData { Timestamp = 1710000000, Open = 1.23, Low = 1.22, High = 1.24, Close = 1.235 },
                        new CandlestickData { Timestamp = 1710000060, Open = 1.235, Low = 1.23, High = 1.25, Close = 1.24 }
                    ]
                });

            // Credential login/signup: never run real Playwright in tests.
            services.RemoveAll<IBinollaCredentialAuth>();
            var credAuth = new Mock<IBinollaCredentialAuth>();
            credAuth
                .Setup(c => c.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BinollaCapturedSession("""42["authorization",{"isDemo":true,"token":"cred-login-token-abcdef"}]""", null));
            credAuth
                .Setup(c => c.SignUpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BinollaCapturedSession("""42["authorization",{"isDemo":true,"token":"cred-signup-token-abcdef"}]""", null));
            services.AddSingleton(credAuth.Object);

            Client.Setup(c => c.PlaceOrderAsync(
                    It.IsAny<string>(),
                    It.IsAny<TradeDirection>(),
                    It.IsAny<decimal>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string asset, TradeDirection dir, decimal amount, int dur, CancellationToken _) =>
                    new OrderResponse
                    {
                        OrderId = "binolla-order-" + Guid.NewGuid().ToString("N")[..8],
                        Asset = asset,
                        Direction = dir,
                        Amount = amount,
                        ExpiryTime = DateTimeOffset.UtcNow.AddSeconds(dur),
                        PlacedAt = DateTimeOffset.UtcNow,
                        Status = OrderStatus.Open,
                        BalanceType = AccountType.Demo,
                        RequestId = 1
                    });
            Client.Setup(c => c.WaitOutcomeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string orderId, CancellationToken _) => new TradeOutcome
                {
                    OrderId = orderId,
                    ProfitLoss = 0.8m,
                    Result = TradeResult.Win,
                    ClosedAt = DateTimeOffset.UtcNow
                });

            SessionManager.Setup(m => m.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ReturnsAsync((string userId, string _, CancellationToken _, string? __) =>
                {
                    ConnectedUsers[userId] = 1;
                    return Client.Object;
                });
            SessionManager.Setup(m => m.Get(It.IsAny<string>()))
                .Returns((string userId) => ConnectedUsers.ContainsKey(userId) ? Client.Object : null);
            SessionManager.Setup(m => m.DisconnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string userId, CancellationToken ct) =>
                {
                    ConnectedUsers.TryRemove(userId, out var _);
                    return Task.CompletedTask;
                });
            SessionManager.Setup(m => m.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string userId, CancellationToken ct) =>
                {
                    ConnectedUsers.TryRemove(userId, out var _);
                    return Task.CompletedTask;
                });
            SessionManager.SetupGet(m => m.ActiveSessionCount).Returns(() => ConnectedUsers.Count);
            SessionManager.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);

            services.AddSingleton(SessionManager.Object);
        });
    }

    /// <summary>Simulates API process restart: live sessions cleared; DB links unchanged.</summary>
    public void SimulateProcessRestart() => ConnectedUsers.Clear();
}

public static class TelegramInitDataHelper
{
    public static string CreateValidInitData(
        string botToken,
        long telegramUserId,
        string username = "alice",
        string firstName = "Alice")
    {
        var userJson = JsonSerializer.Serialize(new
        {
            id = telegramUserId,
            username,
            first_name = firstName
        });

        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = authDate,
            ["query_id"] = "AAEAAAE",
            ["user"] = userJson
        };

        var dataCheckString = string.Join('\n', fields.Select(kv => $"{kv.Key}={kv.Value}"));
        using var secretHmac = new HMACSHA256(Encoding.UTF8.GetBytes("WebAppData"));
        var secretKey = secretHmac.ComputeHash(Encoding.UTF8.GetBytes(botToken));
        using var dataHmac = new HMACSHA256(secretKey);
        var hash = Convert.ToHexString(dataHmac.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString)))
            .ToLowerInvariant();

        var query = string.Join('&',
            fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")
                .Append($"hash={hash}"));
        return query;
    }
}

public sealed class AuthApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AuthApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Invalid_initData_is_rejected()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData = "user=%7B%7D&hash=deadbeef" });
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("TELEGRAM_AUTH_INVALID");
        body.Should().NotContain(ApiFactory.BotToken);
    }

    [Fact]
    public async Task Valid_initData_creates_user_and_returns_jwt()
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, 1001);
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("userId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Same_telegram_user_returns_same_userId()
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, 2002, "bob");
        var r1 = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        var r2 = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        var j1 = await r1.Content.ReadFromJsonAsync<JsonElement>();
        var j2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        j1.GetProperty("userId").GetString().Should().Be(j2.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task Missing_jwt_is_rejected_on_me()
    {
        var res = await _client.GetAsync("/api/me");
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Jwt_authentication_works_for_me()
    {
        var token = await LoginAsync(3003);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("3003");
        body.Should().NotContain("EncryptedSsid");
        body.Should().NotContain("ssid");
    }

    private async Task<string> LoginAsync(long telegramId)
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, telegramId);
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString()!;
    }
}

public sealed class BinollaApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public BinollaApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Connect_associates_session_with_authenticated_user_and_never_returns_ssid()
    {
        var token = await LoginAsync(4001);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/connect")
        {
            Content = JsonContent.Create(new
            {
                ssid = """42["authorization",{"isDemo":true,"token":"SECRET_SSID_VALUE"}]""",
                accountType = "Demo"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("\"connected\":true");
        body.Should().NotContain("SECRET_SSID_VALUE");
        body.Should().NotContain("ssid");

        // encrypted at rest for this authenticated user
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.TelegramUserId == 4001);
        var link = await db.BinollaLinks.SingleAsync(l => l.UserId == user.Id);
        link.EncryptedSsid.Should().NotBeNullOrWhiteSpace();
        link.EncryptedSsid.Should().NotContain("SECRET_SSID_VALUE");

        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        protector.Decrypt(link.EncryptedSsid).Should().Contain("authorization");
    }

    [Fact]
    public async Task Demo_balance_endpoint_works()
    {
        var token = await LoginAsync(4002);
        await ConnectAsync(token);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/binolla/balance");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("currentBalance").GetDecimal().Should().Be(1000m);
    }

    [Fact]
    public async Task Real_trading_is_rejected()
    {
        var token = await LoginAsync(4003);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/connect")
        {
            Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"x\"}]", accountType = "Real" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("REAL_TRADING_DISABLED");
    }

    [Fact]
    public async Task User_A_cannot_access_User_B_trade()
    {
        var tokenA = await LoginAsync(5001);
        var tokenB = await LoginAsync(5002);
        await ConnectAsync(tokenA);

        using var place = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
        {
            Content = JsonContent.Create(new
            {
                asset = "EURUSD_otc",
                direction = "CALL",
                amount = 1,
                durationSeconds = 60
            })
        };
        place.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        place.Headers.Add("Idempotency-Key", "key-a-1");
        var placed = await _client.SendAsync(place);
        placed.EnsureSuccessStatusCode();
        var tradeId = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        using var steal = new HttpRequestMessage(HttpMethod.Get, $"/api/trades/{tradeId}");
        steal.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var stolen = await _client.SendAsync(steal);
        stolen.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    private async Task ConnectAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/connect")
        {
            Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);
    }

    private async Task<string> LoginAsync(long telegramId)
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, telegramId);
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
}

public sealed class TradeApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public TradeApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Duplicate_idempotency_key_does_not_create_duplicate_trade()
    {
        var token = await LoginAndConnectAsync(6001);
        var payload = new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 60 };

        async Task<JsonElement> Place(string key)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Idempotency-Key", key);
            var res = await _client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync<JsonElement>();
        }

        var t1 = await Place("same-key");
        var t2 = await Place("same-key");
        t1.GetProperty("id").GetString().Should().Be(t2.GetProperty("id").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.TelegramUserId == 6001);
        (await db.Trades.CountAsync(t => t.UserId == user.Id && t.IdempotencyKey == "same-key")).Should().Be(1);
    }

    [Fact]
    public async Task Invalid_trade_request_rejected()
    {
        var token = await LoginAndConnectAsync(6002);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
        {
            Content = JsonContent.Create(new { asset = "", direction = "CALL", amount = 0, durationSeconds = 60 })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Idempotency-Key", "bad-1");
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Disconnected_session_returns_controlled_error()
    {
        // Login without connect â†’ no BinollaLink / no session
        var token = await LoginAsync(6003);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
        {
            Content = JsonContent.Create(new { asset = "EURUSD_otc", direction = "PUT", amount = 1, durationSeconds = 60 })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Idempotency-Key", "disc-1");
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("BINOLLA_NOT_CONNECTED");
    }

    [Fact]
    public async Task Binolla_login_with_credentials_connects_without_ssid_paste()
    {
        var token = await LoginAsync(7101);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/login")
        {
            Content = JsonContent.Create(new
            {
                email = "trader@example.com",
                password = "secret-pass",
                accountType = "Demo"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var raw = await res.Content.ReadAsStringAsync();
        raw.ToLowerInvariant().Should().NotContain("ssid");
        raw.ToLowerInvariant().Should().NotContain("password");
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("connected").GetBoolean().Should().BeTrue();
        body.GetProperty("accountType").GetString().Should().Be("Demo");
        body.GetProperty("approvalStatus").GetString().Should().Be("Pending");
    }

    [Fact]
    public async Task Binolla_signup_with_credentials_connects_without_ssid_paste()
    {
        var token = await LoginAsync(7102);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/signup")
        {
            Content = JsonContent.Create(new
            {
                email = "newtrader@example.com",
                password = "secret-pass",
                accountType = "Demo"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("connected").GetBoolean().Should().BeTrue();
        body.GetProperty("access").GetString().Should().Be("AdminApprovalRequired");
    }

    [Fact]
    public async Task Binolla_login_rejects_missing_email()
    {
        var token = await LoginAsync(7103);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/login")
        {
            Content = JsonContent.Create(new { email = "", password = "secret-pass" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("VALIDATION_ERROR");
    }

    [Fact]
    public void Encryption_roundtrip_and_ssid_not_in_ciphertext()
    {
        var protector = new AesGcmSecretProtector(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BINOLLA_TOKEN_ENCRYPTION_KEY"] = ApiFactory.EncryptionKey
            }).Build());

        var cipher = protector.Encrypt("secret-ssid-value");
        cipher.Should().NotContain("secret-ssid-value");
        protector.Decrypt(cipher).Should().Be("secret-ssid-value");
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
}

