using System.Collections.Concurrent;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Base class encapsulating common remote polling workflow for FTP/SFTP sources.
/// Responsibilities: iterate configured sources, connect, list files, track observation snapshots, apply readiness, enqueue events.
/// Backoff and telemetry hooks are virtual for future enhancement.
/// </summary>
public abstract class RemotePollerBase : IFilePoller
{
    private readonly IFileEventQueue _queue;
    private readonly ILogger _logger;
    private readonly IOptionsMonitor<RemoteFileSourcesOptions> _remoteOptions;
    private readonly ConcurrentDictionary<string, FileObservationSnapshot> _observations = new();

    protected RemotePollerBase(IFileEventQueue queue, IOptionsMonitor<RemoteFileSourcesOptions> remoteOptions, ILogger logger)
    {
        _queue = queue;
        _remoteOptions = remoteOptions;
        _logger = logger;
        _remoteOptions.OnChange(_ => OnOptionsChanged());
    }

    protected virtual void OnOptionsChanged()
    {
        // Optionally prune observations for removed sources later.
    }

    public async Task<Result> PollAsync(CancellationToken ct)
    {
        var cycleStart = DateTimeOffset.UtcNow;
        using var cycleActivity = Common.Telemetry.TelemetryInstrumentation.ActivitySource.StartActivity("poll.remote.cycle", System.Diagnostics.ActivityKind.Internal);
        var sources = GetEnabledSources();
        if (sources.Count == 0)
        {
            cycleActivity?.SetTag("sources.count", 0);
            return Result.Success();
        }
        cycleActivity?.SetTag("sources.count", sources.Count);
        Common.Telemetry.TelemetryInstrumentation.PollCycles.Add(1);
        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await PollSourceAsync(src, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll of remote source {SourceName} failed", src.Name);
                Common.Telemetry.TelemetryInstrumentation.PollSourceErrors.Add(1, KeyValuePair.Create<string, object?>("poll.source", src.Name));
            }
        }
        var elapsed = (DateTimeOffset.UtcNow - cycleStart).TotalMilliseconds;
        Common.Telemetry.TelemetryInstrumentation.PollCycleDurationMs.Record(elapsed);
        return Result.Success();
    }

    private async Task PollSourceAsync(IRemoteFileSourceDescriptor source, CancellationToken ct)
    {
        using var sourceActivity = Common.Telemetry.TelemetryInstrumentation.ActivitySource.StartActivity("poll.remote.source", System.Diagnostics.ActivityKind.Internal);
        sourceActivity?.SetTag("poll.source", source.Name);
        await using var client = CreateClient(source);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to {Protocol} source {Name}@{Host}:{Port}", client.Protocol, source.Name, client.Host, client.Port);
            return;
        }

        var readiness = new SizeStableReadinessChecker(TimeSpan.FromSeconds(Math.Max(0, source.MinStableSeconds)));

        await foreach (var file in client.ListFilesAsync(source.RemotePath, source.Recursive, source.Pattern, ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested) break;
            if (file.IsDirectory) continue; // safety
            var key = ProtocolIdentity.BuildKey(MapProtocolType(client.Protocol), client.Host, client.Port, file.FullPath);
            var previous = _observations.TryGetValue(key, out var snap) ? snap : null;
            var ready = await readiness.IsReadyAsync(file, previous, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var newSnap = new FileObservationSnapshot(file.Size, file.LastWriteTimeUtc, previous?.FirstObservedUtc ?? now, now);
            _observations[key] = newSnap;
            if (!ready) continue;
            if (previous is not null && previous.LastObservedUtc >= newSnap.LastObservedUtc)
            {
                continue; // unchanged duplicate in same cycle
            }
            await EnqueueEventAsync(source, client, file, key, ct).ConfigureAwait(false);
        }
    }

    private async Task EnqueueEventAsync(IRemoteFileSourceDescriptor source, IRemoteFileClient client, IRemoteFileInfo file, string identityKey, CancellationToken ct)
    {
        var metadata = new FileMetadata(
            SourcePath: identityKey,
            SizeBytes: file.Size,
            LastModifiedUtc: file.LastWriteTimeUtc.UtcDateTime,
            HashAlgorithm: "none",
            Checksum: null);

        var destination = source.DestinationPath ?? file.FullPath; // will be mapped later by processor
        var ev = new FileEvent(
            Id: Guid.NewGuid().ToString("N"),
            Metadata: metadata,
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: client.Protocol.ToString().ToLowerInvariant(),
            DestinationPath: destination);
        var result = await _queue.EnqueueAsync(ev, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Failed to enqueue remote file event {Key}: {Error}", identityKey, result.Error);
        }
        else
        {
            _logger.LogDebug("Enqueued remote file {Key} ({Protocol})", identityKey, client.Protocol);
            Common.Telemetry.TelemetryInstrumentation.FilesDiscovered.Add(1, KeyValuePair.Create<string, object?>("file.protocol", client.Protocol.ToString().ToLowerInvariant()));
        }
    }

    protected abstract List<IRemoteFileSourceDescriptor> GetEnabledSources();
    protected abstract IRemoteFileClient CreateClient(IRemoteFileSourceDescriptor source);
    protected abstract ProtocolType MapProtocolType(ProtocolType protocol);

    protected interface IRemoteFileSourceDescriptor
    {
        string Name { get; }
        string RemotePath { get; }
        string Pattern { get; }
        bool Recursive { get; }
        int MinStableSeconds { get; }
        string? DestinationPath { get; }
        // credential material already resolved; secret refs are not exposed here
        string Host { get; }
        int Port { get; }
    }
}
