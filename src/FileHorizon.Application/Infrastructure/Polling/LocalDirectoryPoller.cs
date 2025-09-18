using System.Collections.Concurrent;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Polls configured local directories for files matching patterns and enqueues new file events.
/// Simple, non-recursive change detection using last seen snapshot; future improvements may use FS watchers.
/// </summary>
public sealed class LocalDirectoryPoller : IFilePoller
{
    private readonly IFileEventQueue _queue;
    private readonly ILogger<LocalDirectoryPoller> _logger;
    private readonly IOptionsMonitor<FileSourcesOptions> _sourcesOptions;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _disabledSources = new(StringComparer.OrdinalIgnoreCase);

    public LocalDirectoryPoller(
        IFileEventQueue queue,
        ILogger<LocalDirectoryPoller> logger,
        IOptionsMonitor<FileSourcesOptions> sourcesOptions)
    {
        _queue = queue;
        _logger = logger;
        _sourcesOptions = sourcesOptions;
        // When configuration changes, clear disabled sources so new/updated paths are re-evaluated
        _sourcesOptions.OnChange(_ => _disabledSources.Clear());
    }

    public async Task<Result> PollAsync(CancellationToken ct)
    {
        var sources = _sourcesOptions.CurrentValue?.Sources ?? new();
        if (sources.Count == 0)
        {
            _logger.LogDebug("No file sources configured - skipping local directory poll.");
            return Result.Success();
        }

        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested) break;
            // Normalize key for disabled tracking
            var sourceKey = source.Path ?? string.Empty;
            if (_disabledSources.ContainsKey(sourceKey))
            {
                _logger.LogTrace("Skipping previously disabled source {SourceName} ({Path})", source.Name, source.Path);
                continue;
            }

            if (string.IsNullOrWhiteSpace(source.Path) || !Directory.Exists(source.Path))
            {
                _logger.LogWarning("Disabling source {SourceName} - path invalid or does not exist: {Path}", source.Name, source.Path);
                _disabledSources[sourceKey] = 1;
                continue;
            }

            try
            {
                var searchOption = source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                // For now support only single pattern (could extend to split on ';')
                var pattern = string.IsNullOrWhiteSpace(source.Pattern) ? "*.*" : source.Pattern;
                var files = Directory.EnumerateFiles(source.Path, pattern, searchOption);
                foreach (var file in files)
                {
                    if (ct.IsCancellationRequested) break;
                    FileInfo fi;
                    try
                    {
                        fi = new FileInfo(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to stat file {File}", file);
                        continue;
                    }

                    // Stability check: ensure last write time hasn't changed within MinStableSeconds window
                    var lastWrite = fi.LastWriteTimeUtc;
                    var size = fi.Length;
                    var stabilityWindow = TimeSpan.FromSeconds(Math.Max(0, source.MinStableSeconds));
                    if (DateTime.UtcNow - lastWrite < stabilityWindow)
                    {
                        _logger.LogTrace("Skipping unstable file {File} (mtime {MTimeUtc})", fi.FullName, lastWrite);
                        continue;
                    }

                    var key = fi.FullName;
                    var discovered = new DateTimeOffset(lastWrite, TimeSpan.Zero);
                    // Use last write time as a simple version; if we've seen a newer or same version skip
                    if (_seenFiles.TryGetValue(key, out var prev) && prev >= discovered)
                    {
                        continue; // already processed this version
                    }

                    // Update snapshot early to avoid duplicates in same poll cycle
                    _seenFiles[key] = discovered;

                    var id = Guid.NewGuid().ToString("N");
                    var metadata = new FileMetadata(
                        SourcePath: fi.FullName,
                        SizeBytes: size,
                        LastModifiedUtc: lastWrite,
                        HashAlgorithm: "none",
                        Checksum: null);
                    var ev = new FileEvent(
                        Id: id,
                        Metadata: metadata,
                        DiscoveredAtUtc: DateTimeOffset.UtcNow,
                        Protocol: "local",
                        DestinationPath: fi.FullName // placeholder until mapping to destination root
                    );
                    var enqueueResult = await _queue.EnqueueAsync(ev, ct).ConfigureAwait(false);
                    if (!enqueueResult.IsSuccess)
                    {
                        _logger.LogWarning("Failed to enqueue local file {File}: {Error}", fi.FullName, enqueueResult.Error);
                    }
                    else
                    {
                        _logger.LogDebug("Enqueued local file event {FileId} from {File}", id, fi.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling source {SourceName} at path {Path}", source.Name, source.Path);
            }
        }

        return Result.Success();
    }
}
