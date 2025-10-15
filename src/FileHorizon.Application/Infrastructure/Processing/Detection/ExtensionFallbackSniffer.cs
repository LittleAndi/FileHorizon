using FileHorizon.Application.Abstractions;

namespace FileHorizon.Application.Infrastructure.Processing.Detection;

/// <summary>
/// Wrap existing extension detector as a low-confidence sniffer.
/// </summary>
internal sealed class ExtensionFallbackSniffer(IFileTypeDetector extensionDetector) : IContentSniffer
{
    private readonly IFileTypeDetector _extensionDetector = extensionDetector;

    public ContentSniffResult? TryDetect(string? fileNameOrPath, ReadOnlySpan<byte> sample)
    {
        var mime = _extensionDetector.Detect(fileNameOrPath);
        if (mime is null) return null;
        // Low confidence: can be overridden by real content analysis later.
        return new ContentSniffResult(mime, 40);
    }
}