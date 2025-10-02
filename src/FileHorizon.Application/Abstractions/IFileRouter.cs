using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Abstractions;

public interface IFileRouter
{
    Task<Result<IReadOnlyList<DestinationPlan>>> RouteAsync(FileEvent fileEvent, CancellationToken ct);
}
