using FileHorizon.Application.Abstractions;
using System.Collections.Concurrent;

namespace FileHorizon.Application.Infrastructure.Idempotency;

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    // Value is the expiry instant; null means the marker never expires.
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _seen = new();

    public Task<bool> IsProcessedAsync(string key, CancellationToken ct)
    {
        return Task.FromResult(IsActive(key));
    }

    public Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        var expiry = ttl is { } t ? DateTimeOffset.UtcNow + t : (DateTimeOffset?)null;
        while (true)
        {
            if (_seen.TryAdd(key, expiry))
            {
                return Task.FromResult(true);
            }
            if (_seen.TryGetValue(key, out var existing))
            {
                if (existing is null || existing > DateTimeOffset.UtcNow)
                {
                    return Task.FromResult(false); // active marker already present
                }
                // Expired marker: replace it and treat as newly created.
                if (_seen.TryUpdate(key, expiry, existing))
                {
                    return Task.FromResult(true);
                }
            }
            // Lost a race; retry.
        }
    }

    private bool IsActive(string key)
    {
        return _seen.TryGetValue(key, out var expiry)
            && (expiry is null || expiry > DateTimeOffset.UtcNow);
    }
}
