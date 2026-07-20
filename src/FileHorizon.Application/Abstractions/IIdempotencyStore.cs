using System.Threading;
using System.Threading.Tasks;

namespace FileHorizon.Application.Abstractions;

public interface IIdempotencyStore
{
    /// <summary>
    /// Read-only check: returns true if the key is currently marked processed (and not expired).
    /// </summary>
    Task<bool> IsProcessedAsync(string key, CancellationToken ct);

    /// <summary>
    /// Try to mark the given key as processed. Returns true if this call created the marker (i.e., not seen before),
    /// false if the key already exists. A null ttl means the marker never expires.
    /// </summary>
    Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct);
}
