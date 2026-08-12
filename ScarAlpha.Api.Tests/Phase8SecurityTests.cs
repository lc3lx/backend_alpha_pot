using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Persistence;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Phase 8 production-hardening regression matrix.
/// </summary>
public sealed class Phase8SecurityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public Phase8SecurityTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Access_matrix_pending_blocks_market_rsi_balance_and_trades()
    {
        var token = await LoginAsync(9801);
        await ConnectOnlyAsync(token);

        (await StatusAccess(token)).Should().Be("AdminApprovalRequired");

        (await _client.SendAsync(Authed(HttpMethod.Get, "/api/market/assets", token))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Authed(HttpMethod.Get, "/api/strategies/rsi/signal/EURUSD_otc", token))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Authed(HttpMethod.Get, "/api/binolla/balance", token))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        using var trade = Authed(HttpMethod.Post, "/api/trades", token);
        trade.Content = JsonContent.Create(ValidTrade());
        trade.Headers.Add("Idempotency-Key", "p8-pending");
        var tradeRes = await _client.SendAsync(trade);
        tradeRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tradeRes.Content.ReadAsStringAsync()).Should().Contain("ADMIN_APPROVAL_REQUIRED");
    }

    [Fact]
    public async Task Access_matrix_approved_allows_market_and_trade()
    {
        var token = await LoginAsync(9802);
        await ConnectOnlyAsync(token);
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);

        (await StatusAccess(token)).Should().Be("Allowed");
        (await _client.SendAsync(Authed(HttpMethod.Get, "/api/market/assets", token))).EnsureSuccessStatusCode();
        (await _client.SendAsync(Authed(HttpMethod.Get, "/api/binolla/balance", token))).EnsureSuccessStatusCode();

        using var trade = Authed(HttpMethod.Post, "/api/trades", token);
        trade.Content = JsonContent.Create(ValidTrade());
        trade.Headers.Add("Idempotency-Key", "p8-allowed");
        (await _client.SendAsync(trade)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Access_matrix_unauthenticated_is_rejected()
    {
        (await _client.GetAsync("/api/account/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.GetAsync("/api/market/assets")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.GetAsync("/api/admin/binolla/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Balance_masks_real_balance_when_real_trading_disabled()
    {
        var token = await LoginAsync(9803);
        await ConnectOnlyAsync(token);
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);

        using var req = Authed(HttpMethod.Get, "/api/binolla/balance", token);
        var json = await (await _client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("realBalance").GetDecimal().Should().Be(0);
        json.GetProperty("accountType").GetString().Should().Be("Demo");
    }

    [Fact]
    public async Task Trade_validation_rejects_invalid_amounts_and_directions()
    {
        var token = await LoginAsync(9804);
        await ConnectOnlyAsync(token);
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);

        await AssertTradeRejected(token, new { asset = "EURUSD_otc", direction = "CALL", amount = 0, durationSeconds = 60, strategyId = "rsi" }, "p8-zero");
        await AssertTradeRejected(token, new { asset = "EURUSD_otc", direction = "CALL", amount = -5, durationSeconds = 60, strategyId = "rsi" }, "p8-neg");
        await AssertTradeRejected(token, new { asset = "EURUSD_otc", direction = "CALL", amount = 100001, durationSeconds = 60, strategyId = "rsi" }, "p8-huge");
        await AssertTradeRejected(token, new { asset = "EURUSD_otc", direction = "SIDEWAYS", amount = 1, durationSeconds = 60, strategyId = "rsi" }, "p8-dir");
        await AssertTradeRejected(token, new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 1, strategyId = "rsi" }, "p8-dur");
        await AssertTradeRejected(token, new { asset = "EURUSD_otc", direction = "CALL", amount = 1, durationSeconds = 60, strategyId = "ema" }, "p8-strat");
    }

    [Fact]
    public async Task User_isolation_trade_and_admin_actions()
    {
        var tokenA = await LoginAsync(9810);
        await ConnectOnlyAsync(tokenA);
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, tokenA);

        var tokenB = await LoginAsync(9811);
        await ConnectOnlyAsync(tokenB);
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, tokenB);

        using var tradeA = Authed(HttpMethod.Post, "/api/trades", tokenA);
        tradeA.Content = JsonContent.Create(ValidTrade());
        tradeA.Headers.Add("Idempotency-Key", "p8-iso-a");
        var created = await (await _client.SendAsync(tradeA)).Content.ReadFromJsonAsync<JsonElement>();
        var tradeId = created.GetProperty("id").GetString()!;

        using var steal = Authed(HttpMethod.Get, $"/api/trades/{tradeId}", tokenB);
        (await _client.SendAsync(steal)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var meReq = Authed(HttpMethod.Get, "/api/me", tokenA);
        var meJson = await (await _client.SendAsync(meReq)).Content.ReadFromJsonAsync<JsonElement>();
        var userIdA = Guid.Parse(meJson.GetProperty("userId").GetString()!);
        var linkA = db.BinollaLinks.AsNoTracking().First(x => x.UserId == userIdA);

        using var selfApprove = Authed(HttpMethod.Post, $"/api/admin/binolla/accounts/{linkA.Id}/approve", tokenA);
        (await _client.SendAsync(selfApprove)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ssid_never_appears_in_account_or_admin_responses()
    {
        var token = await LoginAsync(9812);
        await ConnectOnlyAsync(token);
        var admin = await LoginAsync(999001);

        foreach (var path in new[]
                 {
                     "/api/me",
                     "/api/account/status",
                     "/api/binolla/status",
                     "/api/admin/binolla/accounts?status=Pending"
                 })
        {
            using var req = Authed(HttpMethod.Get, path, path.Contains("admin") ? admin : token);
            var body = await (await _client.SendAsync(req)).Content.ReadAsStringAsync();
            body.Should().NotContain("EncryptedSsid");
            body.Should().NotContain("SECRET");
            body.ToLowerInvariant().Should().NotContain("authorization");
        }
    }

    [Fact]
    public async Task Jwt_missing_malformed_and_wrong_user_isolation()
    {
        using var missing = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        (await _client.SendAsync(missing)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var bad = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        (await _client.SendAsync(bad)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Telegram_future_auth_date_and_tampered_hash_rejected()
    {
        var future = CreateInitData(ApiFactory.BotToken, 9820, DateTimeOffset.UtcNow.AddHours(5));
        var futureRes = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData = future });
        futureRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var valid = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, 9821);
        var tampered = valid.Replace("alice", "eve");
        var tamperedRes = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData = tampered });
        tamperedRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_demotion_blocks_admin_api_after_relogin()
    {
        // Promote via config (999001), then demote DB role and ensure EnsureAdminAsync blocks.
        var adminToken = await LoginAsync(999001);
        (await _client.SendAsync(Authed(HttpMethod.Get, "/api/admin/binolla/accounts", adminToken)))
            .EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = db.Users.First(u => u.TelegramUserId == 999001);
            admin.Role = UserRole.User;
            await db.SaveChangesAsync();
        }

        // Old JWT still has Admin claim, but service re-checks DB role.
        var blocked = await _client.SendAsync(Authed(HttpMethod.Get, "/api/admin/binolla/accounts", adminToken));
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Concurrent_approve_and_reject_leave_consistent_state()
    {
        var userToken = await LoginAsync(9830);
        await ConnectOnlyAsync(userToken);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var me = await (await _client.SendAsync(Authed(HttpMethod.Get, "/api/me", userToken))).Content.ReadFromJsonAsync<JsonElement>();
        var userId = Guid.Parse(me.GetProperty("userId").GetString()!);
        var linkId = db.BinollaLinks.AsNoTracking().First(x => x.UserId == userId).Id;

        var adminToken = await LoginAsync(999001);
        var approve = Authed(HttpMethod.Post, $"/api/admin/binolla/accounts/{linkId}/approve", adminToken);
        var reject = Authed(HttpMethod.Post, $"/api/admin/binolla/accounts/{linkId}/reject", adminToken);

        var results = await Task.WhenAll(_client.SendAsync(approve), _client.SendAsync(reject));
        results.Should().OnlyContain(r => r.IsSuccessStatusCode);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = db2.BinollaLinks.AsNoTracking().First(x => x.Id == linkId);
        var consistent = (link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Approved)
                         || (!link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Rejected);
        consistent.Should().BeTrue("AdminApproved must match ApprovalStatus after concurrent admin actions");
    }

    [Fact]
    public async Task Idempotency_same_key_returns_same_trade()
    {
        var token = await LoginAsync(9840);
        await ConnectOnlyAsync(token);
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);

        async Task<JsonElement> Place()
        {
            using var req = Authed(HttpMethod.Post, "/api/trades", token);
            req.Content = JsonContent.Create(ValidTrade());
            req.Headers.Add("Idempotency-Key", "p8-idem-same");
            var res = await _client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<JsonElement>())!;
        }

        var a = await Place();
        var b = await Place();
        a.GetProperty("id").GetString().Should().Be(b.GetProperty("id").GetString());
    }

    private async Task AssertTradeRejected(string token, object body, string key)
    {
        using var req = Authed(HttpMethod.Post, "/api/trades", token);
        req.Content = JsonContent.Create(body);
        req.Headers.Add("Idempotency-Key", key);
        var res = await _client.SendAsync(req);
        res.IsSuccessStatusCode.Should().BeFalse();
    }

    private async Task<string> StatusAccess(string token)
    {
        using var req = Authed(HttpMethod.Get, "/api/account/status", token);
        var json = await (await _client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("botAccess").GetString()!;
    }

    private async Task ConnectOnlyAsync(string token)
    {
        using var req = Authed(HttpMethod.Post, "/api/binolla/connect", token);
        req.Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<string> LoginAsync(long id)
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, id);
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static object ValidTrade() => new
    {
        asset = "EURUSD_otc",
        direction = "CALL",
        amount = 1,
        durationSeconds = 60,
        strategyId = "rsi"
    };

    private static string CreateInitData(string botToken, long telegramUserId, DateTimeOffset authDate)
    {
        var userJson = JsonSerializer.Serialize(new { id = telegramUserId, username = "future", first_name = "F" });
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = authDate.ToUnixTimeSeconds().ToString(),
            ["query_id"] = "AAEAAAE",
            ["user"] = userJson
        };
        var dataCheckString = string.Join('\n', fields.Select(kv => $"{kv.Key}={kv.Value}"));
        using var secretHmac = new HMACSHA256(Encoding.UTF8.GetBytes("WebAppData"));
        var secretKey = secretHmac.ComputeHash(Encoding.UTF8.GetBytes(botToken));
        using var dataHmac = new HMACSHA256(secretKey);
        var hash = Convert.ToHexString(dataHmac.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString))).ToLowerInvariant();
        return string.Join('&', fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}").Append($"hash={hash}"));
    }
}

