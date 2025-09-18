using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Abstractions;

public interface IFileProcessor
{
    Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct);
}