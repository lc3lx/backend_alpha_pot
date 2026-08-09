using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Security;
using Xunit;

namespace ScarAlpha.Api.Tests;

public sealed class Phase9SessionRestoreTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public Phase9SessionRestoreTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Restore_one_approved_user_after_simulated_restart_returns_Allowed()
    {
        var token = await LoginAsync(9101);
        await ConnectAsync(token, "42[\"authorization\",{\"token\":\"restore-one-ssid\"}]");
        await ApproveFromTokenAsync(token);

        _factory.SimulateProcessRestart();
        _factory.ConnectedUsers.Should().BeEmpty();

        var restorer = _factory.Services.GetRequiredService<IBinollaSessionRestorer>();
        await restorer.RestoreApprovedSessionsAsync();

        _factory.ConnectedUsers.Should().ContainKey(await UserIdAsync(token));

        using var status = Authed(HttpMethod.Get, "/api/account/status", token);
        var res = await _client.SendAsync(status);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("botAccess").GetString().Should().Be("Allowed");
        json.GetProperty("binollaConnected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Restore_ten_approved_users_reconnects_all()
    {
        var tokens = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var token = await LoginAsync(9200 + i);
            await ConnectAsync(token, $"42[\"authorization\",{{\"token\":\"restore-ten-{i}\"}}]");
            await ApproveFromTokenAsync(token);
            tokens.Add(token);
        }

        _factory.SimulateProcessRestart();
        var restorer = _factory.Services.GetRequiredService<IBinollaSessionRestorer>();
        await restorer.RestoreApprovedSessionsAsync();

        foreach (var token in tokens)
        {
            var userId = await UserIdAsync(token);
            _factory.ConnectedUsers.Should().ContainKey(userId);

            using var status = Authed(HttpMethod.Get, "/api/account/status", token);
            var res = await _client.SendAsync(status);
            res.EnsureSuccessStatusCode();
            (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("botAccess").GetString()
                .Should().Be("Allowed");
        }
    }

    [Fact]
    public async Task Restore_skips_pending_users()
    {
        var pendingToken = await LoginAsync(9301);
        await ConnectAsync(pendingToken, "42[\"authorization\",{\"token\":\"pending-ssid\"}]");
        // not approved

        var approvedToken = await LoginAsync(9302);
        await ConnectAsync(approvedToken, "42[\"authorization\",{\"token\":\"approved-ssid\"}]");
        await ApproveFromTokenAsync(approvedToken);

        _factory.SimulateProcessRestart();
        var restorer = _factory.Services.GetRequiredService<IBinollaSessionRestorer>();
        await restorer.RestoreApprovedSessionsAsync();

        _factory.ConnectedUsers.Should().NotContainKey(await UserIdAsync(pendingToken));
        _factory.ConnectedUsers.Should().ContainKey(await UserIdAsync(approvedToken));
    }

    [Fact]
    public async Task Restore_expired_ssid_marks_SessionExpired_and_continues()
    {
        var okToken = await LoginAsync(9401);
        await ConnectAsync(okToken, "42[\"authorization\",{\"token\":\"ok-ssid\"}]");
        await ApproveFromTokenAsync(okToken);

        var badToken = await LoginAsync(9402);
        await ConnectAsync(badToken, "42[\"authorization\",{\"token\":\"bad-ssid\"}]");
        await ApproveFromTokenAsync(badToken);
        var badUserId = await UserIdAsync(badToken);

        _factory.SimulateProcessRestart();

        _factory.SessionManager
            .Setup(m => m.GetOrCreateAsync(badUserId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BinollaAuthenticationException("SSID expired"));

        var restorer = _factory.Services.GetRequiredService<IBinollaSessionRestorer>();
        await restorer.RestoreApprovedSessionsAsync();

        // Good user restored; bad user skipped without crashing the wave.
        _factory.ConnectedUsers.Should().ContainKey(await UserIdAsync(okToken));
        _factory.ConnectedUsers.Should().NotContainKey(badUserId);

        using var badStatus = Authed(HttpMethod.Get, "/api/account/status", badToken);
        var badRes = await _client.SendAsync(badStatus);
        badRes.EnsureSuccessStatusCode();
        (await badRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("botAccess").GetString()
            .Should().Be("SessionExpired");

        // Reset mock for other tests sharing the factory.
        _factory.SessionManager
            .Setup(m => m.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, string _, CancellationToken _) =>
            {
                _factory.ConnectedUsers[userId] = 1;
                return _factory.Client.Object;
            });
    }

    [Fact]
    public async Task Restore_failed_reconnect_does_not_crash_and_retries_then_skips()
    {
        var token = await LoginAsync(9501);
        await ConnectAsync(token, "42[\"authorization\",{\"token\":\"flaky-ssid\"}]");
        await ApproveFromTokenAsync(token);
        var userId = await UserIdAsync(token);

        _factory.SimulateProcessRestart();

        var attempts = 0;
        _factory.SessionManager
            .Setup(m => m.GetOrCreateAsync(userId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attempts++;
                throw new BinollaConnectionException("temporary failure");
            });

        var restorer = _factory.Services.GetRequiredService<IBinollaSessionRestorer>();
        var act = async () => await restorer.RestoreApprovedSessionsAsync();
        await act.Should().NotThrowAsync();

        attempts.Should().BeGreaterThanOrEqualTo(1);
        _factory.ConnectedUsers.Should().NotContainKey(userId);

        _factory.SessionManager
            .Setup(m => m.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string uid, string _, CancellationToken _) =>
            {
                _factory.ConnectedUsers[uid] = 1;
                return _factory.Client.Object;
            });
    }

    [Fact]
    public async Task Lazy_restore_on_account_status_after_restart()
    {
        var token = await LoginAsync(9601);
        await ConnectAsync(token, "42[\"authorization\",{\"token\":\"lazy-ssid\"}]");
        await ApproveFromTokenAsync(token);

        _factory.SimulateProcessRestart();
        _factory.ConnectedUsers.Should().BeEmpty();

        using var status = Authed(HttpMethod.Get, "/api/account/status", token);
        var res = await _client.SendAsync(status);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("botAccess").GetString().Should().Be("Allowed");
        _factory.ConnectedUsers.Should().ContainKey(await UserIdAsync(token));
    }

    [Fact]
    public void Encrypted_ssid_roundtrip_never_exposes_plaintext_in_ciphertext()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:BinollaTokenEncryptionKey"] = ApiFactory.EncryptionKey
            })
            .Build();
        var protector = new AesGcmSecretProtector(config);
        const string ssid = "42[\"authorization\",{\"token\":\"secret-never-log\"}]";
        var cipher = protector.Encrypt(ssid);
        cipher.Should().NotContain("secret-never-log");
        protector.Decrypt(cipher).Should().Be(ssid);
    }

    private async Task ApproveFromTokenAsync(string token) =>
        await TestAccessHelpers.ApproveFromTokenAsync(_factory, _client, token);

    private async Task ConnectAsync(string token, string ssid)
    {
        using var req = Authed(HttpMethod.Post, "/api/binolla/connect", token);
        req.Content = JsonContent.Create(new { ssid, accountType = "Demo" });
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<string> LoginAsync(long telegramId)
    {
        var initData = TelegramInitDataHelper.CreateValidInitData(ApiFactory.BotToken, telegramId);
        var res = await _client.PostAsJsonAsync("/api/auth/telegram", new { initData });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private async Task<string> UserIdAsync(string token)
    {
        using var me = Authed(HttpMethod.Get, "/api/me", token);
        var res = await _client.SendAsync(me);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString()!;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
