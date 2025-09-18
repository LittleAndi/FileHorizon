using FileHorizon.Application.Models;
using FileHorizon.Application.Common;

namespace FileHorizon.Application.Abstractions;

public interface IFileEventQueue
{
    Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct);
    IAsyncEnumerable<FileEvent> DequeueAsync(CancellationToken ct);
}