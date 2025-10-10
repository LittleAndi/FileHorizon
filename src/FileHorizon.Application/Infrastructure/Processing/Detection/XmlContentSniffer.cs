using FileHorizon.Application.Configuration;

namespace FileHorizon.Application.Infrastructure.Processing.Detection;

internal sealed class XmlContentSniffer : IContentSniffer
{
    private readonly ContentDetectionOptions _options;
    public XmlContentSniffer(ContentDetectionOptions options) => _options = options;

    public ContentSniffResult? TryDetect(string? fileNameOrPath, ReadOnlySpan<byte> sample)
    {
        if (!_options.EnableXml) return null;
        if (sample.IsEmpty) return null;
        var span = sample;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];
        int i = 0;
        while (i < span.Length && (span[i] == (byte)' ' || span[i] == (byte)'\t' || span[i] == (byte)'\r' || span[i] == (byte)'\n')) i++;
        if (i >= span.Length) return null;
        if (span[i] != (byte)'<') return null;
        if (span.Length - i >= 5 && span.Slice(i, 5).SequenceEqual("<?xml"u8))
        {
            return new ContentSniffResult("application/xml", 95);
        }
        var search = span.Slice(i, Math.Min(512, span.Length - i));
        var close = search.IndexOf((byte)'>');
        if (close <= 1) return null;
        byte firstName = span[i + 1];
        bool validStart = (firstName >= 'A' && firstName <= 'Z') || (firstName >= 'a' && firstName <= 'z') || firstName == ':' || firstName == '_';
        if (!validStart) return null;
        return new ContentSniffResult("application/xml", 80);
    }
}