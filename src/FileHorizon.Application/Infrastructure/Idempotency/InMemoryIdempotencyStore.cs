using FileHorizon.Application.Abstractions;
using System.Collections.Concurrent;

namespace FileHorizon.Application.Infrastructure.Idempotency;

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();

    public Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var added = _seen.TryAdd(key, now);
        return Task.FromResult(added);
    }
}
