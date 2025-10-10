namespace FileHorizon.Application.Abstractions;

/// <summary>
/// Detects a suitable MIME content type for a file based on a small sample or its name.
/// Current initial implementation will rely on file extension only; future versions may inspect bytes.
/// Return null when the content type cannot be determined (caller should fallback to application/octet-stream).
/// </summary>
public interface IFileTypeDetector
{
    /// <summary>
    /// Attempts to detect a content type.
    /// </summary>
    /// <param name="fileNameOrPath">File name or full path (may be null).</param>
    /// <param name="sample">Optional leading bytes of the file for signature sniffing (may be empty).</param>
    /// <returns>MIME type string or null if unknown.</returns>
    string? Detect(string? fileNameOrPath, ReadOnlySpan<byte> sample = default);
}