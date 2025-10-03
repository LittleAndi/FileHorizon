using FileHorizon.Application.Models;

namespace FileHorizon.Application.Tests.TestSupport;

internal static class FileEventBuilder
{
    public static FileEvent FromPath(string path)
    {
        var fi = new FileInfo(path);
        var meta = new FileMetadata(fi.FullName, fi.Length, fi.LastWriteTimeUtc, "none", null);
        return new FileEvent(Guid.NewGuid().ToString("N"), meta, DateTimeOffset.UtcNow, "local", fi.FullName, false);
    }
}
