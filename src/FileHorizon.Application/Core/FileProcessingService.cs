using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Core;

public interface IFileProcessingService
{
    Task<Result> HandleAsync(FileEvent fileEvent, CancellationToken ct);
}

public sealed class FileProcessingService : IFileProcessingService
{
    private readonly IFileProcessor _fileProcessor;
    private readonly ILogger<FileProcessingService> _logger;

    public FileProcessingService(IFileProcessor fileProcessor, ILogger<FileProcessingService> logger)
    {
        _fileProcessor = fileProcessor;
        _logger = logger;
    }

    public async Task<Result> HandleAsync(FileEvent fileEvent, CancellationToken ct)
    {
        _logger.LogDebug("Processing file event {FileId} protocol={Protocol} path={Path}", fileEvent.Id, fileEvent.Protocol, fileEvent.Metadata.SourcePath);
        var result = await _fileProcessor.ProcessAsync(fileEvent, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("File event {FileId} failed: {Error}", fileEvent.Id, result.Error);
        }
        else
        {
            _logger.LogDebug("File event {FileId} processed successfully", fileEvent.Id);
        }
        return result;
    }
}