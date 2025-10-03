using System.Threading;
using System.Threading.Tasks;

namespace FileHorizon.Application.Abstractions;

public interface IIdempotencyStore
{
    /// <summary>
    /// Try to mark the given key as processed. Returns true if this call created the marker (i.e., not seen before),
    /// false if the key already exists.
    /// </summary>
    Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct);
}
