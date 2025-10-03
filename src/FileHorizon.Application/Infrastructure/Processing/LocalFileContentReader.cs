using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Processing;

public sealed class LocalFileContentReader(ILogger<LocalFileContentReader> logger) : IFileContentReader
{
    private readonly ILogger<LocalFileContentReader> _logger = logger;

    public Task<Result<FileAttributesInfo>> GetAttributesAsync(FileReference file, CancellationToken ct)
    {
        try
        {
            if (!string.Equals(file.Scheme, "local", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Result<FileAttributesInfo>.Failure(Error.Validation.Invalid($"LocalFileContentReader received non-local scheme '{file.Scheme}'")));
            }
            var path = file.Path;
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                return Task.FromResult(Result<FileAttributesInfo>.Failure(Error.File.NotFound(path)));
            }
            var info = new FileAttributesInfo(
                Size: fi.Length,
                LastWriteUtc: fi.LastWriteTimeUtc,
                Hash: null);
            return Task.FromResult(Result<FileAttributesInfo>.Success(info));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting attributes for {Path}", file.Path);
            return Task.FromResult(Result<FileAttributesInfo>.Failure(Error.Unspecified("LocalReader.Attrs", ex.Message)));
        }
    }

    public Task<Result<Stream>> OpenReadAsync(FileReference file, CancellationToken ct)
    {
        try
        {
            if (!string.Equals(file.Scheme, "local", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Result<Stream>.Failure(Error.Validation.Invalid($"LocalFileContentReader received non-local scheme '{file.Scheme}'")));
            }
            var path = file.Path;
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult(Result<Stream>.Success(fs));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(Result<Stream>.Failure(Error.File.NotFound(file.Path)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening read stream for {Path}", file.Path);
            return Task.FromResult(Result<Stream>.Failure(Error.Unspecified("LocalReader.Open", ex.Message)));
        }
    }
}
