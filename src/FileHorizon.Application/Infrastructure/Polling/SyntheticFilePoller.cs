using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Synthetic poller for early development: emits a single FileEvent per invocation.
/// Later replaced by real filesystem / endpoint watchers.
/// </summary>
public sealed class SyntheticFilePoller : IFilePoller
{
    private readonly IFileEventQueue _queue;
    private readonly ILogger<SyntheticFilePoller> _logger;

    public SyntheticFilePoller(IFileEventQueue queue, ILogger<SyntheticFilePoller> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public async Task<Result> PollAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Polling cancelled before execution");
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
        _logger.LogTrace("Generated synthetic file event {FileId}", id);
        var result = await _queue.EnqueueAsync(fe, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Failed to enqueue synthetic event {FileId}: {Error}", id, result.Error);
        }
        return result;
    }
}
