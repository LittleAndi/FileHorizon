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
        var sources = _sourcesOptions.CurrentValue?.Sources;
        if (sources is null || sources.Count == 0)
        {
            _logger.LogDebug("No file sources configured - skipping local directory poll.");
            return Result.Success();
        }

        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessSourceAsync(source, ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    private async Task ProcessSourceAsync(FileSourceOptions source, CancellationToken ct)
    {
        var sourceKey = source.Path ?? string.Empty;

        if (_disabledSources.ContainsKey(sourceKey))
        {
            _logger.LogTrace("Skipping previously disabled source {SourceName} ({Path})", source.Name, source.Path);
            return;
        }

        if (string.IsNullOrWhiteSpace(source.Path) || !Directory.Exists(source.Path))
        {
            _logger.LogWarning("Disabling source {SourceName} - path invalid or does not exist: {Path}", source.Name, source.Path);
            _disabledSources[sourceKey] = 1;
            return;
        }

        try
        {
            await EnumerateAndProcessFilesAsync(source, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling source {SourceName} at path {Path}", source.Name, source.Path);
        }
    }

    private async Task EnumerateAndProcessFilesAsync(FileSourceOptions source, CancellationToken token)
    {
        var searchOption = source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pattern = string.IsNullOrWhiteSpace(source.Pattern) ? "*.*" : source.Pattern;

        foreach (var file in Directory.EnumerateFiles(source.Path!, pattern, searchOption))
        {
            if (token.IsCancellationRequested) break;
            await ProcessFileAsync(file, source, token).ConfigureAwait(false);
        }
    }

    private async Task ProcessFileAsync(string file, FileSourceOptions source, CancellationToken token)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(file);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stat file {File}", file);
            return;
        }

        var lastWrite = fi.LastWriteTimeUtc;
        var stabilityWindow = TimeSpan.FromSeconds(Math.Max(0, source.MinStableSeconds));
        if (DateTime.UtcNow - lastWrite < stabilityWindow)
        {
            _logger.LogTrace("Skipping unstable file {File} (mtime {MTimeUtc})", fi.FullName, lastWrite);
            return;
        }

        var key = fi.FullName;
        var discovered = new DateTimeOffset(lastWrite, TimeSpan.Zero);
        if (_seenFiles.TryGetValue(key, out var prev) && prev >= discovered)
        {
            return; // already processed this version
        }

        _seenFiles[key] = discovered; // snapshot early

        var metadata = new FileMetadata(
            SourcePath: fi.FullName,
            SizeBytes: fi.Length,
            LastModifiedUtc: lastWrite,
            HashAlgorithm: "none",
            Checksum: null);

        var ev = new FileEvent(
            Id: Guid.NewGuid().ToString("N"),
            Metadata: metadata,
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: "local",
            DestinationPath: fi.FullName
        );

        var enqueueResult = await _queue.EnqueueAsync(ev, token).ConfigureAwait(false);
        if (!enqueueResult.IsSuccess)
        {
            _logger.LogWarning("Failed to enqueue local file {File}: {Error}", fi.FullName, enqueueResult.Error);
        }
        else
        {
            _logger.LogDebug("Enqueued local file event {FileId} from {File}", ev.Id, fi.FullName);
        }
    }
}
