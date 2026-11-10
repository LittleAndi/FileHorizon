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
    private readonly TimeProvider _time;

    // The queue is shared by every source, so its rejections pause the whole poller rather than one source.
    // Polls never overlap, so plain fields are enough.
    private int _queueRejections;
    private DateTimeOffset _queuePausedUntilUtc;

    /// <summary>How many generated keys a seeding run reports back for inspection.</summary>
    private const int SeedSampleKeyCount = 5;

    /// <param name="timeProvider">Clock for source backoff and the queue pause; defaults to the system clock.</param>
    protected RemotePollerBase(IFileEventQueue queue, IOptionsMonitor<RemoteFileSourcesOptions> remoteOptions, ILogger logger, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
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

        var queuePauseRemaining = _queuePausedUntilUtc - _time.GetUtcNow();
        if (queuePauseRemaining > TimeSpan.Zero)
        {
            _logger.LogTrace("Skipping remote poll cycle: queue pause active. remainingSeconds={RemainingSeconds}", (int)queuePauseRemaining.TotalSeconds);
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

            var queueAccepting = await PollSourceAsync(src, ct).ConfigureAwait(false);

            // NEW: per-source completion log
            _logger.LogTrace("Completed poll for source {Name}", src.Name);

            // The next source would hit the same queue; leave the rest of the cycle until the pause is over.
            if (!queueAccepting) break;
        }

        var elapsed = (DateTimeOffset.UtcNow - cycleStart).TotalMilliseconds;
        Common.Telemetry.TelemetryInstrumentation.PollCycleDurationMs.Record(elapsed);

        // NEW: cycle end log
        _logger.LogTrace("Remote poll cycle end. elapsedMs={ElapsedMs}", elapsed);

        return Result.Success();
    }

    /// <returns>False when the queue rejected a file for a reason that is not specific to that file, which ends the cycle.</returns>
    private async Task<bool> PollSourceAsync(IRemoteFileSourceDescriptor source, CancellationToken ct)
    {
        using var sourceActivity = Common.Telemetry.TelemetryInstrumentation.ActivitySource.StartActivity("poll.remote.source", System.Diagnostics.ActivityKind.Internal);
        sourceActivity?.SetTag("poll.source", source.Name);

        _logger.LogTrace("Listing files from source {Name} path={Path} recursive={Recursive} pattern={Pattern}",
            source.Name, source.RemotePath, source.Recursive, source.Pattern);

        IRemoteFileClient createdClient;
        try
        {
            createdClient = CreateClient(source);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // Same as connect and listing below: an error raised while shutdown is in progress is cancellation.
            throw new OperationCanceledException("Polling was cancelled while creating the source client.", ex, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Client creation resolves secrets, which can fail or time out (a timeout surfaces as
            // TaskCanceledException while ct is not cancelled). Like a failed connect, that belongs to this
            // source and must not abort the cycle for the sources after it.
            _logger.LogWarning(ex, "Failed to create {Protocol} client for source {Name}@{Host}:{Port}", source.Protocol, source.Name, source.Host, source.Port);
            RecordSourceError(source);
            RegisterFailure(source.Name);
            return true;
        }

        await using var client = createdClient;
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // Same as the listing below: an error raised while shutdown is in progress is cancellation, not a source failure.
            throw new OperationCanceledException("Polling was cancelled while connecting to the source.", ex, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to connect to {Protocol} source {Name}@{Host}:{Port}", source.Protocol, source.Name, client.Host, client.Port);
            RecordSourceError(source);
            RegisterFailure(source.Name);
            return true;
        }

        // Track window and counters
        var window = TimeSpan.FromSeconds(Math.Max(0, source.MinStableSeconds));
        var readiness = new SizeStableReadinessChecker(window);

        var seen = 0;
        var readyCount = 0;
        var unstableCount = 0;
        var duplicateCount = 0;
        var invalidCount = 0;
        (string Key, Error Error)? rejection = null;

        try
        {
            await foreach (var file in client.ListFilesAsync(source.RemotePath, source.Recursive, source.Pattern, ct).ConfigureAwait(false))
            {
                seen++;
                if (ct.IsCancellationRequested) break;
                if (file.IsDirectory) continue;

                var key = ProtocolIdentity.BuildKey(source.Protocol, client.Host, client.Port, file.FullPath);
                _observations.TryGetValue(key, out var prev);
                // Only the exact dispatched version counts; a changed size/mtime is a new version to re-dispatch.
                var alreadyDispatched = _dispatched.TryGetValue(key, out var dispatchedVersion)
                    && dispatchedVersion.Size == file.Size
                    && dispatchedVersion.MTime == file.LastWriteTimeUtc;

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
                    Common.Telemetry.TelemetryInstrumentation.FilesSkippedUnstable.Add(1, KeyValuePair.Create<string, object?>("file.protocol", ProtocolTag(source)));
                    continue;
                }

                if (alreadyDispatched)
                {
                    duplicateCount++;
                    _logger.LogTrace("Suppressing duplicate dispatch for {Key}", key);
                    continue;
                }

                var enqueued = await EnqueueEventAsync(source, file, key, ct).ConfigureAwait(false);
                if (enqueued.IsFailure && enqueued.Error.IsValidation)
                {
                    // The queue will never accept this version of the file (e.g. a listing without a parsable
                    // size). Record it so it is not offered again until it changes, and carry on with the rest.
                    _logger.LogWarning("Skipping remote file {Key}: the queue rejected it as invalid ({Error}). It is not offered again unless its size or mtime changes.", key, enqueued.Error);
                    _dispatched[key] = (newSnap.Size, newSnap.LastWriteTimeUtc);
                    invalidCount++;
                    continue;
                }

                if (enqueued.IsFailure)
                {
                    // Not recorded as dispatched, so the file is offered again on a later poll. This is
                    // at-least-once: a failure reported after the queue had in fact stored the event (e.g. a Redis
                    // timeout) leads to a second event for the same version, which the idempotency check absorbs.
                    // Stop here: the fault is in the queue, so the next file would be rejected too.
                    rejection = (key, enqueued.Error);
                    break;
                }

                _queueRejections = 0;
                _dispatched[key] = (newSnap.Size, newSnap.LastWriteTimeUtc);
                readyCount++;
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // Shutdown can surface from SSH.NET as a connection or disposal error while a synchronous call is
            // blocked. That is not a source failure: report it as the cancellation it is.
            throw new OperationCanceledException("Polling was cancelled while listing the source.", ex, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A failed listing or enqueue (including an SFTP operation timeout on a half-open connection) must not abort
            // the cycle for other sources. Files seen before the failure keep their observations and dispatches.
            _logger.LogError(ex, "Poll failed for {Protocol} source {Name}@{Host}:{Port} path={Path} after {Seen} item(s)",
                source.Protocol, source.Name, client.Host, client.Port, source.RemotePath, seen);
            RecordSourceError(source);
            RegisterFailure(source.Name);
            return true;
        }

        // A listing cut short by shutdown is neither a success nor a failure: leave backoff and logs alone.
        if (ct.IsCancellationRequested) return true;

        if (rejection is { } rejected)
        {
            // The source itself answered, so it gets no error count and no backoff: queue.enqueue.failures is
            // the signal for this, and the pause covers every source of this poller.
            var pause = RegisterQueueRejection();
            _logger.LogWarning("Queue rejected {Key} from {Protocol} source {Name}: {Error}. Remote polling paused for {PauseSeconds}s; the file is offered again afterwards. itemsSeen={Seen}, enqueued={Ready}",
                rejected.Key, source.Protocol, source.Name, rejected.Error, (int)pause.TotalSeconds, seen, readyCount);
            return false;
        }

        ResetBackoff(source.Name);

        _logger.LogDebug("Completed listing for source {Name}. itemsSeen={Seen}, ready={Ready}, unstable={Unstable}, duplicate={Duplicate}, invalid={Invalid}",
            source.Name, seen, readyCount, unstableCount, duplicateCount, invalidCount);
        return true;
    }

    private static string ProtocolTag(IRemoteFileSourceDescriptor source) => source.Protocol.ToString().ToLowerInvariant();

    private static void RecordSourceError(IRemoteFileSourceDescriptor source)
        => Common.Telemetry.TelemetryInstrumentation.PollSourceErrors.Add(1,
            KeyValuePair.Create<string, object?>("file.protocol", ProtocolTag(source)),
            KeyValuePair.Create<string, object?>("poll.source", source.Name));

    /// <summary>Offers the file to the queue. A failed result is returned for the caller to log and act on.</summary>
    private async Task<Result> EnqueueEventAsync(IRemoteFileSourceDescriptor source, IRemoteFileInfo file, string identityKey, CancellationToken ct)
    {
        var metadata = BuildMetadata(file, identityKey);

        var destination = source.DestinationPath ?? file.FullPath; // will be mapped later by processor
        var ev = new FileEvent(
            Id: Guid.NewGuid().ToString("N"),
            Metadata: metadata,
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: ProtocolTag(source),
            DestinationPath: destination,
            DeleteAfterTransfer: source.DeleteAfterTransfer);
        var result = await _queue.EnqueueAsync(ev, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogDebug("Enqueued remote file {Key} ({Protocol})", identityKey, source.Protocol);
            Common.Telemetry.TelemetryInstrumentation.FilesDiscovered.Add(1, KeyValuePair.Create<string, object?>("file.protocol", ProtocolTag(source)));
        }

        return result;
    }

    /// <summary>
    /// Builds the metadata identifying one specific version of a remote file. Shared by the dispatch
    /// path and <see cref="SeedIdempotencyAsync"/> so the two can never produce different identity keys.
    /// </summary>
    private static FileMetadata BuildMetadata(IRemoteFileInfo file, string identityKey)
        => new(
            SourcePath: identityKey,
            SizeBytes: file.Size,
            LastModifiedUtc: file.LastWriteTimeUtc.UtcDateTime,
            HashAlgorithm: "none",
            Checksum: null);

    /// <summary>
    /// Marks every file currently present on the named source as already transferred, without moving any
    /// data. Used to adopt a source that already holds a backlog: existing files are suppressed and only
    /// files arriving afterwards are picked up.
    /// </summary>
    /// <remarks>
    /// Markers are written through <see cref="IIdempotencyStore"/> rather than composed by hand, so the
    /// stored format is whatever the configured store uses. A file that is mid-upload when seeded is
    /// harmless: the completed file has a different size or mtime and therefore a different key.
    /// </remarks>
    public async Task<Result<IdempotencySeedResult>> SeedIdempotencyAsync(
        IdempotencySeedRequest request,
        IIdempotencyStore store,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);

        var source = GetEnabledSources()
            .FirstOrDefault(s => string.Equals(s.Name, request.SourceName, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return Result<IdempotencySeedResult>.Failure(
                Error.Validation.Invalid($"No enabled source named '{request.SourceName}' is configured for this poller."));
        }

        var pattern = string.IsNullOrWhiteSpace(request.PatternOverride) ? source.Pattern : request.PatternOverride!;
        var scanned = 0;
        var marked = 0;
        var alreadyMarked = 0;
        var samples = new List<string>();

        try
        {
            await using var client = CreateClient(source);
            await client.ConnectAsync(ct).ConfigureAwait(false);

            await foreach (var file in client.ListFilesAsync(source.RemotePath, source.Recursive, pattern, ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                if (file.IsDirectory) continue;

                var identityKey = ProtocolIdentity.BuildKey(source.Protocol, client.Host, client.Port, file.FullPath);
                var idempotencyKey = FileIdentity.BuildIdempotencyKey(BuildMetadata(file, identityKey));

                scanned++;
                if (samples.Count < SeedSampleKeyCount)
                {
                    samples.Add(idempotencyKey);
                }

                if (request.DryRun) continue;

                if (await store.TryMarkProcessedAsync(idempotencyKey, request.Ttl, ct).ConfigureAwait(false))
                {
                    marked++;
                }
                else
                {
                    alreadyMarked++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Markers written before the failure are durable and correct; report progress with the error
            // so a partial run can simply be repeated.
            _logger.LogError(ex, "Idempotency seed for source {Source} failed after scanning {Scanned} file(s)", source.Name, scanned);
            return Result<IdempotencySeedResult>.Failure(
                Error.Unspecified("Idempotency.SeedFailed", $"Seeding '{source.Name}' failed after {scanned} file(s): {ex.Message}"));
        }

        _logger.LogInformation(
            "Idempotency seed for source {Source} (pattern={Pattern}, dryRun={DryRun}): scanned={Scanned}, marked={Marked}, alreadyMarked={AlreadyMarked}",
            source.Name, pattern, request.DryRun, scanned, marked, alreadyMarked);

        return Result<IdempotencySeedResult>.Success(
            new IdempotencySeedResult(source.Name, pattern, scanned, marked, alreadyMarked, samples));
    }

    protected abstract List<IRemoteFileSourceDescriptor> GetEnabledSources();
    protected abstract IRemoteFileClient CreateClient(IRemoteFileSourceDescriptor source);

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
        ProtocolType Protocol { get; }
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
            var now = _time.GetUtcNow();
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
        var delay = BackoffDelay(state.Failures);
        state.NextAttemptUtc = _time.GetUtcNow() + delay;
        _backoff[sourceName] = state;
        _logger.LogDebug("Source {Source} backoff scheduled for {DelaySeconds}s after {Failures} failures", sourceName, (int)delay.TotalSeconds, state.Failures);
    }

    /// <summary>Exponential delay for the given number of consecutive failures: base doubling up to the cap.</summary>
    private TimeSpan BackoffDelay(int failures)
    {
        var exponent = Math.Min(failures - 1, 6); // cap growth
        var delay = TimeSpan.FromMilliseconds(_baseBackoff.TotalMilliseconds * Math.Pow(2, exponent));
        return delay > _maxBackoff ? _maxBackoff : delay;
    }

    /// <summary>Pauses the whole poller after a queue rejection. The streak ends with the next accepted enqueue.</summary>
    private TimeSpan RegisterQueueRejection()
    {
        var pause = BackoffDelay(++_queueRejections);
        _queuePausedUntilUtc = _time.GetUtcNow() + pause;
        return pause;
    }

    private void ResetBackoff(string sourceName)
    {
        if (_backoff.TryRemove(sourceName, out _))
        {
            _logger.LogTrace("Reset backoff for source {Source}", sourceName);
        }
    }
}
