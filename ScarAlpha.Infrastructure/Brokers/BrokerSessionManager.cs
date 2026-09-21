using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.BrokerGateway;

/// <summary>
/// Routes a user to their broker's client.
///
/// <para>Binolla keeps its own C# session manager underneath — it is live, and wrapping it
/// rather than replacing it means the venue choice can ship without rewriting the
/// implementation that currently carries real trades. Every other broker is served by the
/// Python gateway.</para>
/// </summary>
public sealed class BrokerSessionManager : IBrokerSessionManager
{
    private readonly IBinollaSessionManager _binolla;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BrokerSessionManager> _logger;

    /// <summary>Gateway-backed sessions, keyed by user and broker.</summary>
    private readonly ConcurrentDictionary<string, BrokerGatewayClient> _gatewaySessions = new();

    public BrokerSessionManager(
        IBinollaSessionManager binolla,
        IHttpClientFactory httpFactory,
        ILogger<BrokerSessionManager> logger)
    {
        _binolla = binolla;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public int ActiveSessionCount => _binolla.ActiveSessionCount + _gatewaySessions.Count;

    public IBrokerClient? Get(Guid userId, string broker)
    {
        broker = Brokers.Normalize(broker);

        if (broker == Brokers.Binolla)
        {
            var inner = _binolla.Get(userId.ToString());
            return inner is null ? null : new BinollaBrokerAdapter(inner);
        }

        if (_gatewaySessions.TryGetValue(Key(userId, broker), out var client))
        {
            if (client.Lifecycle is SessionLifecycleState.AuthenticationFailed)
            {
                _gatewaySessions.TryRemove(Key(userId, broker), out _);
                return null;
            }
            return client;
        }

        return null;
    }

    public IBrokerClient? GetAny(string broker)
    {
        broker = Brokers.Normalize(broker);
        if (broker == Brokers.Binolla)
            return null;

        return _gatewaySessions.Values.FirstOrDefault(s =>
            string.Equals(s.Broker, broker, StringComparison.OrdinalIgnoreCase) &&
            s.Lifecycle is not (SessionLifecycleState.Disconnected or SessionLifecycleState.AuthenticationFailed));
    }

    public async Task<IBrokerClient> GetOrCreateAsync(
        Guid userId,
        string broker,
        BrokerCredentials credentials,
        CancellationToken ct = default)
    {
        broker = Brokers.Normalize(broker);

        if (broker == Brokers.Binolla)
        {
            if (string.IsNullOrWhiteSpace(credentials.Ssid))
                throw new ApiException(ApiErrorCodes.ValidationError, "Binolla needs an SSID.");

            var inner = await _binolla.GetOrCreateAsync(
                userId.ToString(),
                credentials.Ssid,
                ct,
                credentials.CookieHeader,
                credentials.AccountType).ConfigureAwait(false);

            return new BinollaBrokerAdapter(inner);
        }

        var key = Key(userId, broker);
        if (_gatewaySessions.TryGetValue(key, out var existing))
        {
            if (existing.IsTransportConnected &&
                existing.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
            {
                return existing;
            }

            try
            {
                await existing.ConnectAsync(credentials, ct).ConfigureAwait(false);
                return existing;
            }
            catch
            {
                _gatewaySessions.TryRemove(key, out _);
            }
        }

        var http = _httpFactory.CreateClient(BrokerGatewayDefaults.HttpClientName);
        var client = new BrokerGatewayClient(http, userId, broker, _logger);
        await client.ConnectAsync(credentials, ct).ConfigureAwait(false);

        _gatewaySessions[key] = client;
        return client;
    }

    public async Task RemoveAsync(Guid userId, string broker, CancellationToken ct = default)
    {
        broker = Brokers.Normalize(broker);

        if (broker == Brokers.Binolla)
        {
            await _binolla.RemoveAsync(userId.ToString(), ct).ConfigureAwait(false);
            return;
        }

        if (_gatewaySessions.TryRemove(Key(userId, broker), out var client))
        {
            try { await client.DisconnectAsync(ct).ConfigureAwait(false); }
            catch { /* best effort */ }
        }
    }

    public async Task RefreshGatewaySessionsAsync(CancellationToken ct = default)
    {
        foreach (var (key, client) in _gatewaySessions.ToArray())
        {
            try
            {
                await client.RefreshTransportAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // RefreshTransportAsync already marks the client dead.
            }

            if (!client.IsTransportConnected &&
                client.Lifecycle is SessionLifecycleState.AuthenticationFailed
                    or SessionLifecycleState.SessionExpired)
            {
                _gatewaySessions.TryRemove(key, out _);
            }
        }
    }

    private static string Key(Guid userId, string broker) => $"{userId:N}:{broker}";
}

public static class BrokerGatewayDefaults
{
    public const string HttpClientName = "broker-gateway";

    /// <summary>
    /// Localhost by default. The gateway holds live trading sessions and must never be
    /// reachable from outside the host.
    /// </summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:8100";
}
