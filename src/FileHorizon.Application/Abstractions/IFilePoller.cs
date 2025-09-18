using FileHorizon.Application.Common;

namespace FileHorizon.Application.Abstractions;

public interface IFilePoller
{
    Task<Result> PollAsync(CancellationToken ct);
}