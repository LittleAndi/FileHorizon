using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Processing;

public sealed class LocalFileSink(ILogger<LocalFileSink> logger) : IFileSink
{
    private readonly ILogger<LocalFileSink> _logger = logger;
    public string Name => "Local";

    public async Task<Result> WriteAsync(FileReference target, Stream content, FileWriteOptions options, CancellationToken ct)
    {
        try
        {
            if (!string.Equals(target.Scheme, "local", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(Error.Validation.Invalid($"LocalFileSink received non-local scheme '{target.Scheme}'"));
            }

            var destPath = ApplyRename(target.Path, options?.RenamePattern);
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var mode = options?.Overwrite == true ? FileMode.Create : FileMode.CreateNew;
            await using var fs = new FileStream(destPath, mode, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await content.CopyToAsync(fs, 64 * 1024, ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (IOException ioEx) when (ioEx is not null)
        {
            _logger.LogError(ioEx, "I/O error writing to {Path}", target.Path);
            return Result.Failure(Error.Unspecified("LocalSink.IO", ioEx.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error writing to {Path}", target.Path);
            return Result.Failure(Error.Unspecified("LocalSink.Unexpected", ex.Message));
        }
    }

    private static string ApplyRename(string path, string? renamePattern)
    {
        if (string.IsNullOrWhiteSpace(renamePattern)) return path;
        var fileName = Path.GetFileName(path);
        var date = DateTimeOffset.UtcNow;
        var replaced = renamePattern
            .Replace("{fileName}", fileName)
            .Replace("{yyyyMMdd}", date.ToString("yyyyMMdd"));
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        return Path.Combine(dir, replaced);
    }
}
