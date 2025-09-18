using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Validation;

public sealed class BasicFileEventValidator : IFileEventValidator
{
    public Result Validate(FileEvent fileEvent)
    {
        if (fileEvent is null) return Result.Failure(Error.Validation.NullFileEvent);
        if (string.IsNullOrWhiteSpace(fileEvent.Id)) return Result.Failure(Error.Validation.EmptyId);
        if (fileEvent.Metadata is null) return Result.Failure(Error.Validation.NullMetadata);
        if (string.IsNullOrWhiteSpace(fileEvent.Metadata.SourcePath)) return Result.Failure(Error.Validation.EmptySourcePath);
        if (fileEvent.Metadata.SizeBytes < 0) return Result.Failure(Error.Validation.NegativeSize);
        return Result.Success();
    }
}
