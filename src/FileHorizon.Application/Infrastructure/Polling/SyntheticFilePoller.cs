using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Synthetic poller for early development: emits a single FileEvent per invocation.
/// Later replaced by real filesystem / endpoint watchers.
/// </summary>
public sealed class SyntheticFilePoller : IFilePoller
{
    private readonly IFileEventQueue _queue;

    public SyntheticFilePoller(IFileEventQueue queue)
    {
        _queue = queue;
    }

    public async Task<Result> PollAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Result.Failure(Error.Unspecified("Poller.Cancelled", "Poll was cancelled"));
        }

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var sourcePath = $"/synthetic/{id}.dat";
        var metadata = new FileMetadata(
            SourcePath: sourcePath,
            SizeBytes: 0,
            LastModifiedUtc: now,
            HashAlgorithm: "none",
            Checksum: null);
        var fe = new FileEvent(
            Id: id,
            Metadata: metadata,
            DiscoveredAtUtc: now,
            Protocol: "synthetic",
            DestinationPath: sourcePath);

        return await _queue.EnqueueAsync(fe, ct).ConfigureAwait(false);
    }
}
