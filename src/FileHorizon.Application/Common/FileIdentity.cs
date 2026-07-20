using System.Globalization;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Common;

/// <summary>
/// Builds the durable idempotency key for a specific version of a file.
/// Same source path + size + last-modified time yields the same key; any change yields a new key,
/// so a modified file is treated as a new version and transferred again.
/// </summary>
public static class FileIdentity
{
    /// <summary>Versioned prefix so the key format can evolve without colliding with older markers.</summary>
    public const string KeyPrefix = "fh:idemp:v2:";

    public static string BuildIdempotencyKey(FileMetadata metadata)
    {
        var mtime = metadata.LastModifiedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return $"{KeyPrefix}{metadata.SourcePath}|{metadata.SizeBytes}|{mtime}";
    }
}
