using System.Collections.Concurrent;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Polling;

public abstract class RemotePollerBase : IFilePoller
{
    private readonly IFileEventQueue _queue;
    private readonly ILogger _logger;
    private readonly IOptionsMonitor<RemoteFileSourcesOptions> _remoteOptions;
    private readonly ConcurrentDictionary<string, FileObservationSnapshot> _observations = new();
    private readonly ConcurrentDictionary<string, (long Size, DateTimeOffset MTime)> _dispatched = new();
    private readonly ConcurrentDictionary<string, BackoffState> _backoff = new();
    private readonly TimeSpan _baseBackoff = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(5);

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

        // NEW: cycle start log
        _logger.LogTrace("Remote poll cycle start. sources={Count}", sources.Count);

        if (sources.Count == 0)
        {
            cycleActivity?.SetTag("sources.count", 0);
            _logger.LogTrace("No enabled remote sources; skipping remote poll cycle.");
            return Result.Success();
        }

        cycleActivity?.SetTag("sources.count", sources.Count);
        Common.Telemetry.TelemetryInstrumentation.PollCycles.Add(1);

        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (IsSourceInBackoff(src.Name, out var remaining))
            {
                // NEW: backoff log
                _logger.LogTrace("Skipping source {Source} due to backoff. remainingSeconds={RemainingSeconds}", src.Name, (int)remaining.TotalSeconds);
                continue;
            }

            // NEW: per-source poll log
            _logger.LogDebug("Polling remote source {Name} host={Host} port={Port} path={RemotePath} recursive={Recursive} pattern={Pattern}",
                src.Name, src.Host, src.Port, src.RemotePath, src.Recursive, src.Pattern);

            await PollSourceAsync(src, ct).ConfigureAwait(false);

            // NEW: per-source completion log
            _logger.LogTrace("Completed poll for source {Name}", src.Name);
        }

        var elapsed = (DateTimeOffset.UtcNow - cycleStart).TotalMilliseconds;
        Common.Telemetry.TelemetryInstrumentation.PollCycleDurationMs.Record(elapsed);

        // NEW: cycle end log
        _logger.LogTrace("Remote poll cycle end. elapsedMs={ElapsedMs}", elapsed);

        return Result.Success();
    }

    private async Task PollSourceAsync(IRemoteFileSourceDescriptor source, CancellationToken ct)
    {
        using var sourceActivity = Common.Telemetry.TelemetryInstrumentation.ActivitySource.StartActivity("poll.remote.source", System.Diagnostics.ActivityKind.Internal);
        sourceActivity?.SetTag("poll.source", source.Name);

        _logger.LogTrace("Listing files from source {Name} path={Path} recursive={Recursive} pattern={Pattern}",
            source.Name, source.RemotePath, source.Recursive, source.Pattern);

        await using var client = CreateClient(source);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to {Protocol} source {Name}@{Host}:{Port}", client.Protocol, source.Name, client.Host, client.Port);
            RegisterFailure(source.Name);
            return;
        }

        // Track window and counters
        var window = TimeSpan.FromSeconds(Math.Max(0, source.MinStableSeconds));
        var readiness = new SizeStableReadinessChecker(window);

        var seen = 0;
        var readyCount = 0;
        var unstableCount = 0;
        var duplicateCount = 0;

        await foreach (var file in client.ListFilesAsync(source.RemotePath, source.Recursive, source.Pattern, ct).ConfigureAwait(false))
        {
            seen++;
            if (ct.IsCancellationRequested) break;
            if (file.IsDirectory) continue;

            var key = ProtocolIdentity.BuildKey(MapProtocolType(client.Protocol), client.Host, client.Port, file.FullPath);
            _observations.TryGetValue(key, out var prev);
            var alreadyDispatched = _dispatched.ContainsKey(key);

            // Explain readiness decision
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                if (prev is null)
                {
                    if (window > TimeSpan.Zero)
                        _logger.LogTrace("First observation for {Key}; waiting windowSec={Window}", key, (int)window.TotalSeconds);
                    else
                        _logger.LogTrace("First observation for {Key}; window=0 so ready if unchanged checks pass", key);
                }
                else
                {
                    var changed = prev.Size != file.Size || prev.LastWriteTimeUtc != file.LastWriteTimeUtc;
                    if (changed)
                    {
                        _logger.LogTrace("Unstable {Key}: changed size/mtime (prev: {PrevSize},{PrevMTime:o} -> curr: {Size},{MTime:o})",
                            key, prev.Size, prev.LastWriteTimeUtc, file.Size, file.LastWriteTimeUtc);
                    }
                    else
                    {
                        var stableForTrace = DateTimeOffset.UtcNow - prev.LastObservedUtc;
                        _logger.LogTrace("Stable {Key}: stableForSec={StableFor}/{WindowSec}", key, (int)stableForTrace.TotalSeconds, (int)window.TotalSeconds);
                    }
                }
            }

            var now = DateTimeOffset.UtcNow;
            var unchanged = prev is not null &&
                            prev.Size == file.Size &&
                            prev.LastWriteTimeUtc == file.LastWriteTimeUtc;

            // Keep the old LastObservedUtc as the stability baseline if unchanged; otherwise reset.
            FileObservationSnapshot newSnap;
            if (unchanged)
            {
                // Do NOT move the baseline; this lets stable duration accumulate.
                newSnap = new FileObservationSnapshot(
                    file.Size,
                    file.LastWriteTimeUtc,
                    prev!.FirstObservedUtc,
                    prev.LastObservedUtc); // preserve last unchanged baseline
            }
            else
            {
                // Content (size/mtime) changed -> reset baseline to now.
                newSnap = new FileObservationSnapshot(
                    file.Size,
                    file.LastWriteTimeUtc,
                    prev?.FirstObservedUtc ?? now,
                    now);
            }

            _observations[key] = newSnap;

            var stableFor = prev is null ? TimeSpan.Zero : (DateTimeOffset.UtcNow - newSnap.LastObservedUtc);
            // Recompute readiness using original previous snapshot (prev) which had the old baseline
            var ready = await readiness.IsReadyAsync(file, prev, ct).ConfigureAwait(false);

            if (!ready)
            {
                unstableCount++;
                Common.Telemetry.TelemetryInstrumentation.FilesSkippedUnstable.Add(1, KeyValuePair.Create<string, object?>("file.protocol", client.Protocol.ToString().ToLowerInvariant()));
                continue;
            }

            if (alreadyDispatched)
            {
                duplicateCount++;
                _logger.LogTrace("Suppressing duplicate dispatch for {Key}", key);
                continue;
            }

            await EnqueueEventAsync(source, client, file, key, ct).ConfigureAwait(false);
            _dispatched[key] = (newSnap.Size, newSnap.LastWriteTimeUtc);
            readyCount++;
        }

        _logger.LogDebug("Completed listing for source {Name}. itemsSeen={Seen}, ready={Ready}, unstable={Unstable}, duplicate={Duplicate}",
            source.Name, seen, readyCount, unstableCount, duplicateCount);
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
            DestinationPath: destination,
            DeleteAfterTransfer: source.DeleteAfterTransfer);
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
        string Host { get; }
        int Port { get; }
        bool DeleteAfterTransfer { get; }
    }

    private sealed class BackoffState
    {
        public int Failures { get; set; }
        public DateTimeOffset NextAttemptUtc { get; set; }
    }

    private bool IsSourceInBackoff(string sourceName, out TimeSpan remaining)
    {
        if (_backoff.TryGetValue(sourceName, out var s))
        {
            var now = DateTimeOffset.UtcNow;
            if (s.NextAttemptUtc > now)
            {
                remaining = s.NextAttemptUtc - now;
                return true;
            }
        }
        remaining = TimeSpan.Zero;
        return false;
    }

    private void RegisterFailure(string sourceName)
    {
        var state = _backoff.AddOrUpdate(sourceName, _ => new BackoffState { Failures = 1 }, (_, existing) => { existing.Failures++; return existing; });
        var exponent = Math.Min(state.Failures - 1, 6); // cap growth
        var delay = TimeSpan.FromMilliseconds(_baseBackoff.TotalMilliseconds * Math.Pow(2, exponent));
        if (delay > _maxBackoff) delay = _maxBackoff;
        state.NextAttemptUtc = DateTimeOffset.UtcNow + delay;
        _backoff[sourceName] = state;
        _logger.LogDebug("Source {Source} backoff scheduled for {DelaySeconds}s after {Failures} failures", sourceName, (int)delay.TotalSeconds, state.Failures);
    }

    private void ResetBackoff(string sourceName)
    {
        if (_backoff.TryRemove(sourceName, out _))
        {
            _logger.LogTrace("Reset backoff for source {Source}", sourceName);
        }
    }
}
