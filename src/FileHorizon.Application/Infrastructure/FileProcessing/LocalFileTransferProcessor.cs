using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.FileProcessing;

/// <summary>
/// Processes a file event by copying or moving the underlying file into the configured destination root.
/// Controlled by feature flags to allow safe incremental enablement.
/// </summary>
public sealed class LocalFileTransferProcessor : IFileProcessor
{
    private readonly ILogger<LocalFileTransferProcessor> _logger;
    private readonly IOptionsMonitor<PipelineFeaturesOptions> _featureOptions;
    private readonly IOptionsMonitor<FileSourcesOptions> _sourcesOptions;

    public LocalFileTransferProcessor(
        ILogger<LocalFileTransferProcessor> logger,
        IOptionsMonitor<PipelineFeaturesOptions> featureOptions,
        IOptionsMonitor<FileSourcesOptions> sourcesOptions)
    {
        _logger = logger;
        _featureOptions = featureOptions;
        _sourcesOptions = sourcesOptions;
    }

    public Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct)
    {
        var features = _featureOptions.CurrentValue;
        if (features is not null && !features.EnableFileTransfer)
        {
            _logger.LogDebug("File transfer disabled - skipping real copy/move for {File}", fileEvent.Metadata.SourcePath);
            return Task.FromResult(Result.Success());
        }

        try
        {
            var sourcePath = fileEvent.Metadata.SourcePath;
            string? destRoot = null; // determined from matching source only

            // Attempt to locate source configuration for potential per-source destination override & move flag
            FileSourceOptions? matchedSource = null;
            try
            {
                var sources = _sourcesOptions.CurrentValue?.Sources ?? new();
                var normalizedFile = Path.GetFullPath(sourcePath);
                foreach (var s in sources)
                {
                    if (string.IsNullOrWhiteSpace(s.Path)) continue;
                    try
                    {
                        var root = Path.GetFullPath(s.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (normalizedFile.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedSource = s;
                            break;
                        }
                    }
                    catch { /* ignore normalization errors */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error resolving source metadata for {File}", fileEvent.Metadata.SourcePath);
            }

            destRoot = matchedSource?.DestinationPath;

            if (string.IsNullOrWhiteSpace(destRoot))
            {
                _logger.LogWarning("No destination path configured for source of {File}; skipping transfer", fileEvent.Metadata.SourcePath);
                return Task.FromResult(Result.Success());
            }
            // sourcePath already captured above
            if (!File.Exists(sourcePath))
            {
                _logger.LogWarning("Source file missing at processing time: {File}", sourcePath);
                return Task.FromResult(Result.Failure(Error.File.NotFound(sourcePath)));
            }

            // Build destination path. For now we only use the file name; future: preserve relative structure.
            var destFileName = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(destRoot, destFileName);

            var allowCreate = matchedSource?.CreateDestinationDirectories ?? true;
            if (!Directory.Exists(destRoot))
            {
                if (allowCreate)
                {
                    try
                    {
                        Directory.CreateDirectory(destRoot);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create destination directory {Dest} for {File}", destRoot, sourcePath);
                        return Task.FromResult(Result.Failure(Error.Unspecified("FileTransfer.DirectoryCreateFailed", ex.Message)));
                    }
                }
                else
                {
                    _logger.LogWarning("Destination directory {Dest} missing and creation disabled for source of {File}; skipping", destRoot, sourcePath);
                    return Task.FromResult(Result.Success());
                }
            }

            // Determine if source config wants move (default false if no match)
            bool move = matchedSource?.MoveAfterProcessing == true;

            if (!move)
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                _logger.LogInformation("Copied file {Source} to {Destination}", sourcePath, destinationPath);
            }
            else
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                _logger.LogInformation("Moved file {Source} to {Destination}", sourcePath, destinationPath);
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error transferring file {File}", fileEvent.Metadata.SourcePath);
            return Task.FromResult(Result.Failure(Error.Unspecified("FileTransfer.Exception", ex.Message)));
        }
    }
}
