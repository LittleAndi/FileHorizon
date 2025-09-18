using FileHorizon.Application.Models;
using FileHorizon.Application.Common;

namespace FileHorizon.Application.Abstractions;

public interface IFileEventQueue
{
    Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct);
    IAsyncEnumerable<FileEvent> DequeueAsync(CancellationToken ct);
    /// <summary>
    /// Non-blocking drain of up to <paramref name="maxCount"/> items currently available.
    /// Returns immediately with 0..N items.
    /// </summary>
    IReadOnlyCollection<FileEvent> TryDrain(int maxCount);
}