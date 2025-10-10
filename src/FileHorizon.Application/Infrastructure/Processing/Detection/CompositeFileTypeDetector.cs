using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Processing.Detection;

internal sealed class CompositeFileTypeDetector : IFileTypeDetector
{
    private readonly IReadOnlyList<IContentSniffer> _sniffers;
    private readonly ContentDetectionOptions _options;

    public CompositeFileTypeDetector(IOptions<ContentDetectionOptions> options, IFileTypeDetector extensionDetector)
    {
        _options = options.Value;
        var list = new List<IContentSniffer>(4)
        {
            new XmlContentSniffer(_options),
            new EdifactContentSniffer(_options),
            new ExtensionFallbackSniffer(extensionDetector)
        };
        _sniffers = list;
    }

    public string? Detect(string? fileNameOrPath, ReadOnlySpan<byte> sample = default)
    {
        ContentSniffResult? best = null;
        foreach (var s in _sniffers)
        {
            var r = s.TryDetect(fileNameOrPath, sample);
            if (r is null) continue;
            if (best is null || r.Value.Confidence > best.Value.Confidence)
            {
                best = r;
                if (r.Value.IsDefinitive) break;
            }
        }
        return best?.MimeType;
    }
}