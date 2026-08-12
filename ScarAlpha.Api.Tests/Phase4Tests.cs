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
using Moq;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Persistence;
using Xunit;
using BinollaTradeDirection = ScarAlpha.Binolla.Models.TradeDirection;

namespace ScarAlpha.Api.Tests;

/// <summary>Pending approval factory â€” connected but not approved.</summary>
public sealed class PendingApprovalApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TELEGRAM_BOT_TOKEN"] = ApiFactory.BotToken,
                ["JWT_SECRET"] = ApiFactory.JwtSecret,
                ["JWT_ISSUER"] = "ScarAlpha",
                ["JWT_AUDIENCE"] = "ScarAlpha.App",
                ["BINOLLA_TOKEN_ENCRYPTION_KEY"] = ApiFactory.EncryptionKey,
                ["Admin:TelegramUserIds"] = "999001"
            });
        });
        builder.ConfigureServices(ConfigureTestDbAndBinolla);
    }

    internal static void ConfigureTestDbAndBinolla(IServiceCollection services)
    {
        var dbName = "ScarAlphaP7Pending_" + Guid.NewGuid().ToString("N");
        foreach (var d in services.Where(x =>
                     x.ServiceType == typeof(AppDbContext) ||
                     x.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList())
            services.Remove(d);
        services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.RemoveAll<IBinollaSessionManager>();
        services.RemoveAll<IBinollaCredentialAuth>();
        var credAuth = new Mock<IBinollaCredentialAuth>();
        credAuth.Setup(c => c.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BinollaCapturedSession("""42["authorization",{"isDemo":true,"token":"cred-login-token-abcdef"}]""", null));
        credAuth.Setup(c => c.SignUpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BinollaCapturedSession("""42["authorization",{"isDemo":true,"token":"cred-signup-token-abcdef"}]""", null));
        services.AddSingleton(credAuth.Object);
        var client = new Mock<IBinollaClient>(MockBehavior.Loose);
        client.SetupGet(c => c.Lifecycle).Returns(SessionLifecycleState.Connected);
        client.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
        client.Setup(c => c.ChangeAccountAsync(It.IsAny<AccountType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        client.Setup(c => c.GetBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new BalanceInfo
        {
            DemoBalance = 1000m, RealBalance = 0m, CurrentType = AccountType.Demo, LastUpdated = DateTimeOffset.UtcNow
        });
        client.Setup(c => c.PlaceOrderAsync(It.IsAny<string>(), It.IsAny<BinollaTradeDirection>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResponse
            {
                OrderId = "x", Asset = "EURUSD_otc", Direction = BinollaTradeDirection.Call, Amount = 1,
                ExpiryTime = DateTimeOffset.UtcNow.AddMinutes(1), PlacedAt = DateTimeOffset.UtcNow,
                Status = OrderStatus.Open, BalanceType = AccountType.Demo, RequestId = 1
            });
        var mgr = new Mock<IBinollaSessionManager>(MockBehavior.Loose);
        var connected = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        mgr.Setup(m => m.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((string uid, string _, CancellationToken _, string? __) => { connected[uid] = 1; return client.Object; });
        mgr.Setup(m => m.Get(It.IsAny<string>())).Returns((string uid) => connected.ContainsKey(uid) ? client.Object : null);
        services.AddSingleton(mgr.Object);
    }
}

public sealed class Phase4BusinessModelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public Phase4BusinessModelTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Strategies_lists_RSI_active_and_others_coming_soon()
    {
        var token = await LoginAsync(8101);
        using var req = Authed(HttpMethod.Get, "/api/strategies", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var strategies = json.GetProperty("strategies").EnumerateArray().ToList();
        strategies.Should().HaveCountGreaterOrEqualTo(3);

        var rsi = strategies.Single(s => s.GetProperty("id").GetString() == "rsi");
        rsi.GetProperty("status").GetString().Should().Be("Active");
        rsi.GetProperty("enabled").GetBoolean().Should().BeTrue();

        var ema = strategies.Single(s => s.GetProperty("id").GetString() == "ema");
        ema.GetProperty("status").GetString().Should().Be("ComingSoon");
        ema.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Connect_then_admin_approve_returns_allowed_access()
    {
        var token = await LoginAsync(8102);
        var body = await ConnectAsync(token, approve: true);
        body.GetProperty("connected").GetBoolean().Should().BeTrue();
        body.GetProperty("adminApproved").GetBoolean().Should().BeTrue();
        body.GetProperty("access").GetString().Should().Be("Allowed");
        body.ToString().Should().NotContain("ssid");
    }

    [Fact]
    public async Task Account_status_reflects_linked_approved_state()
    {
        var token = await LoginAsync(8103);
        await ConnectAsync(token, approve: true);
        using var req = Authed(HttpMethod.Get, "/api/account/status", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("binollaConnected").GetBoolean().Should().BeTrue();
        json.GetProperty("adminApproved").GetBoolean().Should().BeTrue();
        json.GetProperty("botAccess").GetString().Should().Be("Allowed");
    }

    [Fact]
    public async Task Disabled_strategy_cannot_trade()
    {
        var token = await LoginAsync(8104);
        await ConnectAsync(token, approve: true);
        using var req = Authed(HttpMethod.Post, "/api/trades", token);
        req.Content = JsonContent.Create(new
        {
            asset = "EURUSD_otc",
            direction = "CALL",
            amount = 1,
            durationSeconds = 60,
            strategyId = "macd"
        });
        req.Headers.Add("Idempotency-Key", "macd-blocked");
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("STRATEGY_DISABLED");
    }

    private async Task<string> LoginAsync(long id)
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, id);
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private async Task<JsonElement> ConnectAsync(string token, bool approve)
    {
        using var req = Authed(HttpMethod.Post, "/api/binolla/connect", token);
        req.Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" });
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        if (approve)
            await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);

        using var statusReq = Authed(HttpMethod.Get, "/api/account/status", token);
        var statusRes = await _client.SendAsync(statusReq);
        statusRes.EnsureSuccessStatusCode();
        var status = await statusRes.Content.ReadFromJsonAsync<JsonElement>();

        // Return connect-like shape for assertions that check access after approval.
        using var connectAgain = Authed(HttpMethod.Post, "/api/binolla/connect", token);
        connectAgain.Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" });
        var connectRes = await _client.SendAsync(connectAgain);
        connectRes.EnsureSuccessStatusCode();
        return await connectRes.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}

public sealed class Phase4PendingApprovalTests : IClassFixture<PendingApprovalApiFactory>
{
    private readonly HttpClient _client;

    public Phase4PendingApprovalTests(PendingApprovalApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Pending_approval_yields_AdminApprovalRequired()
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, 8201);
        var login = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

        using var connect = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/connect")
        {
            Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" })
        };
        connect.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var connectRes = await _client.SendAsync(connect);
        connectRes.EnsureSuccessStatusCode();
        var connectJson = await connectRes.Content.ReadFromJsonAsync<JsonElement>();
        connectJson.GetProperty("adminApproved").GetBoolean().Should().BeFalse();
        connectJson.GetProperty("access").GetString().Should().Be("AdminApprovalRequired");

        using var status = new HttpRequestMessage(HttpMethod.Get, "/api/account/status");
        status.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var statusJson = await (await _client.SendAsync(status)).Content.ReadFromJsonAsync<JsonElement>();
        statusJson.GetProperty("botAccess").GetString().Should().Be("AdminApprovalRequired");

        using var trade = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
        {
            Content = JsonContent.Create(new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 60, strategyId = "rsi" })
        };
        trade.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        trade.Headers.Add("Idempotency-Key", "pending-ref");
        var tradeRes = await _client.SendAsync(trade);
        tradeRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tradeRes.Content.ReadAsStringAsync()).Should().Contain("ADMIN_APPROVAL_REQUIRED");
    }
}

public sealed class Phase4RejectedTests : IClassFixture<PendingApprovalApiFactory>
{
    private readonly PendingApprovalApiFactory _factory;
    private readonly HttpClient _client;

    public Phase4RejectedTests(PendingApprovalApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Rejected_user_blocked_from_trading()
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, 8301);
        var token = (await (await _client.PostAsJsonAsync("/api/auth/telegram", new { initData })).Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("accessToken").GetString()!;

        using var connect = new HttpRequestMessage(HttpMethod.Post, "/api/binolla/connect")
        {
            Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" })
        };
        connect.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(connect)).EnsureSuccessStatusCode();

        using var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var userId = Guid.Parse((await (await _client.SendAsync(meReq)).Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("userId").GetString()!);
        await TestAccessHelpers.SetApprovalAsync(_factory, userId, AdminApprovalStatus.Rejected);

        using var trade = new HttpRequestMessage(HttpMethod.Post, "/api/trades")
        {
            Content = JsonContent.Create(new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 60, strategyId = "rsi" })
        };
        trade.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        trade.Headers.Add("Idempotency-Key", "not-elig");
        var res = await _client.SendAsync(trade);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("NOT_ELIGIBLE");
    }
}

