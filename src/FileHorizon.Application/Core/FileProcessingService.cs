using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Core;

public interface IFileProcessingService
{
    Task<Result> HandleAsync(FileEvent fileEvent, CancellationToken ct);
}

internal sealed class FileProcessingService : IFileProcessingService
{
    private readonly IFileProcessor _fileProcessor;

    public FileProcessingService(IFileProcessor fileProcessor)
    {
        _fileProcessor = fileProcessor;
    }

    public Task<Result> HandleAsync(FileEvent fileEvent, CancellationToken ct)
        => _fileProcessor.ProcessAsync(fileEvent, ct);
}