namespace FileHorizon.Application.Infrastructure.Processing.Detection;

/// <summary>
/// Internal pluggable content sniffer. Returns a candidate MIME and confidence (0-100) or null if no opinion.
/// </summary>
internal interface IContentSniffer
{
    ContentSniffResult? TryDetect(string? fileNameOrPath, ReadOnlySpan<byte> sample);
}

internal readonly record struct ContentSniffResult(string MimeType, int Confidence)
{
    public bool IsDefinitive => Confidence >= 90; // heuristic threshold
}