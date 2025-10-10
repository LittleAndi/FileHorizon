using FileHorizon.Application.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace FileHorizon.Application.Infrastructure.Processing;

/// <summary>
/// Very small extension -> MIME type mapper. Case-insensitive.
/// Future enhancement: add magic number inspection.
/// </summary>
public sealed class ExtensionFileTypeDetector : IFileTypeDetector
{
    // Minimal starter map; extend as needed.
    private static readonly ConcurrentDictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".edi"] = "application/edifact",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bin"] = "application/octet-stream"
    };

    public string? Detect(string? fileNameOrPath, ReadOnlySpan<byte> sample = default)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return null;
        try
        {
            var ext = Path.GetExtension(fileNameOrPath);
            if (string.IsNullOrEmpty(ext)) return null;
            if (_map.TryGetValue(ext, out var mime)) return mime;
            return null;
        }
        catch
        {
            return null; // be conservative
        }
    }
}