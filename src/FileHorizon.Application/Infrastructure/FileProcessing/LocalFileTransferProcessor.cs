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
    private readonly IOptionsMonitor<FileDestinationOptions> _destOptions;
    private readonly IOptionsMonitor<PipelineFeaturesOptions> _featureOptions;

    public LocalFileTransferProcessor(
        ILogger<LocalFileTransferProcessor> logger,
        IOptionsMonitor<FileDestinationOptions> destOptions,
        IOptionsMonitor<PipelineFeaturesOptions> featureOptions)
    {
        _logger = logger;
        _destOptions = destOptions;
        _featureOptions = featureOptions;
    }

    public Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct)
    {
        if (!_featureOptions.CurrentValue.EnableFileTransfer)
        {
            _logger.LogDebug("File transfer disabled - skipping real copy/move for {File}", fileEvent.Metadata.SourcePath);
            return Task.FromResult(Result.Success());
        }

        try
        {
            var destRoot = _destOptions.CurrentValue.RootPath;
            if (string.IsNullOrWhiteSpace(destRoot))
            {
                _logger.LogWarning("Destination root path not configured; skipping transfer for {File}", fileEvent.Metadata.SourcePath);
                return Task.FromResult(Result.Success());
            }
            var sourcePath = fileEvent.Metadata.SourcePath;
            if (!File.Exists(sourcePath))
            {
                _logger.LogWarning("Source file missing at processing time: {File}", sourcePath);
                return Task.FromResult(Result.Failure(Error.File.NotFound(sourcePath)));
            }

            // Build destination path preserving relative folder structure (for now just file name)
            var destFileName = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(destRoot, destFileName);

            var createDirs = _destOptions.CurrentValue.CreateDirectories;
            if (createDirs)
            {
                Directory.CreateDirectory(destRoot);
            }

            var copyMode = _featureOptions.CurrentValue.CopyInsteadOfMove;
            if (copyMode)
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
