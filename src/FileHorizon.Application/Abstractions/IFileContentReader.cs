using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Abstractions;

public interface IFileContentReader
{
    Task<Result<Stream>> OpenReadAsync(FileReference file, CancellationToken ct);
    Task<Result<FileAttributesInfo>> GetAttributesAsync(FileReference file, CancellationToken ct);
}
