using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Access;
using ScarAlpha.Infrastructure.Persistence;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class Phase7AdminApprovalTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public Phase7AdminApprovalTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Connected_not_approved_is_AdminApprovalRequired()
    {
        var token = await LoginAsync(9101);
        await ConnectOnlyAsync(token);

        using var status = Authed(HttpMethod.Get, "/api/account/status", token);
        var json = await (await _client.SendAsync(status)).Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("binollaConnected").GetBoolean().Should().BeTrue();
        json.GetProperty("adminApproved").GetBoolean().Should().BeFalse();
        json.GetProperty("botAccess").GetString().Should().Be("AdminApprovalRequired");
    }

    [Fact]
    public async Task Approved_user_is_Allowed_and_can_trade_rsi()
    {
        var token = await LoginAsync(9102);
        await ConnectOnlyAsync(token);
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);

        using var status = Authed(HttpMethod.Get, "/api/account/status", token);
        (await (await _client.SendAsync(status)).Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("botAccess").GetString().Should().Be("Allowed");

        using var trade = Authed(HttpMethod.Post, "/api/trades", token);
        trade.Content = JsonContent.Create(new
        {
            asset = "EURUSD_otc",
            direction = "CALL",
            amount = 1,
            durationSeconds = 60,
            strategyId = "rsi"
        });
        trade.Headers.Add("Idempotency-Key", "p7-allowed");
        (await _client.SendAsync(trade)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Normal_user_cannot_list_or_approve()
    {
        var userToken = await LoginAsync(9103);
        await ConnectOnlyAsync(userToken);

        using var list = Authed(HttpMethod.Get, "/api/admin/binolla/accounts?status=Pending", userToken);
        var listRes = await _client.SendAsync(list);
        listRes.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var linkId = db.BinollaLinks.Select(x => x.Id).First();

        using var approve = Authed(HttpMethod.Post, $"/api/admin/binolla/accounts/{linkId}/approve", userToken);
        var approveRes = await _client.SendAsync(approve);
        approveRes.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_can_list_approve_and_audit()
    {
        // Regular user connects
        var userToken = await LoginAsync(9104);
        await ConnectOnlyAsync(userToken);

        // Admin is telegram 999001 (configured in ApiFactory)
        var adminToken = await LoginAsync(999001);

        using var list = Authed(HttpMethod.Get, "/api/admin/binolla/accounts?status=Pending", adminToken);
        var listRes = await _client.SendAsync(list);
        listRes.EnsureSuccessStatusCode();
        var listJson = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        listJson.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        listJson.ToString().Should().NotContain("ssid");
        listJson.ToString().Should().NotContain("Encrypted");

        var linkId = listJson.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("telegramUserId").GetInt64() == 9104)
            .GetProperty("id").GetString()!;

        using var approve = Authed(HttpMethod.Post, $"/api/admin/binolla/accounts/{linkId}/approve", adminToken);
        var approveRes = await _client.SendAsync(approve);
        approveRes.EnsureSuccessStatusCode();
        var approved = await approveRes.Content.ReadFromJsonAsync<JsonElement>();
        approved.GetProperty("adminApproved").GetBoolean().Should().BeTrue();
        approved.GetProperty("approvalStatus").GetString().Should().Be("Approved");
        approved.GetProperty("approvedBy").GetString().Should().NotBeNullOrWhiteSpace();

        // Duplicate approve is safe
        using var approve2 = Authed(HttpMethod.Post, $"/api/admin/binolla/accounts/{linkId}/approve", adminToken);
        (await _client.SendAsync(approve2)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditEvents.Any(e => e.Action == "BinollaAccountApproved").Should().BeTrue();

        using var status = Authed(HttpMethod.Get, "/api/account/status", userToken);
        (await (await _client.SendAsync(status)).Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("botAccess").GetString().Should().Be("Allowed");
    }

    [Fact]
    public async Task Admin_reject_blocks_trading()
    {
        var userToken = await LoginAsync(9105);
        await ConnectOnlyAsync(userToken);
        var adminToken = await LoginAsync(999001);

        using var list = Authed(HttpMethod.Get, "/api/admin/binolla/accounts?status=Pending", adminToken);
        var linkId = (await (await _client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("telegramUserId").GetInt64() == 9105)
            .GetProperty("id").GetString()!;

        using var reject = Authed(HttpMethod.Post, $"/api/admin/binolla/accounts/{linkId}/reject", adminToken);
        (await _client.SendAsync(reject)).EnsureSuccessStatusCode();

        using var trade = Authed(HttpMethod.Post, "/api/trades", userToken);
        trade.Content = JsonContent.Create(new
        {
            asset = "EURUSD_otc",
            direction = "CALL",
            amount = 1,
            durationSeconds = 60,
            strategyId = "rsi"
        });
        trade.Headers.Add("Idempotency-Key", "p7-rejected");
        var tradeRes = await _client.SendAsync(trade);
        tradeRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tradeRes.Content.ReadAsStringAsync()).Should().Contain("NOT_ELIGIBLE");
    }

    [Fact]
    public async Task BotAccessService_maps_states()
    {
        var access = _factory.Services.CreateScope().ServiceProvider.GetRequiredService<ScarAlpha.Application.Abstractions.IBotAccessService>();
        var result = await access.CheckAsync(Guid.NewGuid());
        result.Access.Should().Be(ScarAlpha.Application.Abstractions.BotAccessState.BinollaNotConnected);
    }

    private async Task<string> LoginAsync(long id)
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, id);
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private async Task ConnectOnlyAsync(string token)
    {
        using var req = Authed(HttpMethod.Post, "/api/binolla/connect", token);
        req.Content = JsonContent.Create(new { ssid = "42[\"authorization\",{\"token\":\"demo\"}]", accountType = "Demo" });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
