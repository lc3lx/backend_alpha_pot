using System.Collections.Concurrent;
using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Infrastructure.Security;

public sealed class IdempotencyGate : IIdempotencyGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(Guid userId, string key, CancellationToken ct = default)
    {
        var gateKey = $"{userId:N}:{key}";
        var sem = _locks.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sem;
        private int _released;

        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _sem.Release();
            return ValueTask.CompletedTask;
        }
    }
}
