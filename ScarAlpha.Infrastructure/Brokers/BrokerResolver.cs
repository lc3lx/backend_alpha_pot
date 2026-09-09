using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.BrokerGateway;

public sealed class BrokerResolver : IBrokerResolver
{
    /// <summary>
    /// Short enough that a re-link is picked up quickly even if the invalidation is missed,
    /// long enough that a full scan costs no queries.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<Guid, (string Broker, DateTimeOffset At)> _cache = new();

    public BrokerResolver(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<string> GetAsync(Guid userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
            return hit.Broker;

        string broker;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var links = scope.ServiceProvider.GetRequiredService<IBinollaLinkRepository>();
            var link = await links.GetByUserIdAsync(userId, ct).ConfigureAwait(false);
            broker = Brokers.Normalize(link?.Broker);
        }
        catch
        {
            // A failed lookup must not stop trading: Binolla is where every existing
            // account lives, so it is the safe answer.
            return Brokers.Default;
        }

        _cache[userId] = (broker, DateTimeOffset.UtcNow);
        return broker;
    }

    public void Invalidate(Guid userId) => _cache.TryRemove(userId, out _);
}
