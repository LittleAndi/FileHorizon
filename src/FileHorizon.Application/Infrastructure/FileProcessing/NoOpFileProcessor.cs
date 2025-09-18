using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Infrastructure.FileProcessing;

/// <summary>
/// Temporary no-op file processor to allow end-to-end flow wiring.
/// Simply performs minimal validation and returns success.
/// </summary>
internal sealed class NoOpFileProcessor : IFileProcessor
{
    public Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileEvent.Id))
        {
            return Task.FromResult(Result.Failure(Error.Unspecified("FileEvent.InvalidId", "FileEvent Id was null or whitespace")));
        }
        if (fileEvent.Metadata is null)
        {
            return Task.FromResult(Result.Failure(Error.Unspecified("FileEvent.MetadataNull", "FileEvent Metadata was null")));
        }
        // In a real implementation we might enqueue or persist work here.
        return Task.FromResult(Result.Success());
    }
}
