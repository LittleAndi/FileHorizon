using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Abstractions;

public interface IFileSink
{
    string Name { get; }
    Task<Result> WriteAsync(FileReference target, Stream content, FileWriteOptions options, CancellationToken ct);
}
