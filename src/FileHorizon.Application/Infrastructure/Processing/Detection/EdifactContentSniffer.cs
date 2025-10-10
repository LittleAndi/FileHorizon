using FileHorizon.Application.Configuration;
using System.Text;

namespace FileHorizon.Application.Infrastructure.Processing.Detection;

internal sealed class EdifactContentSniffer : IContentSniffer
{
    private readonly ContentDetectionOptions _options;
    public EdifactContentSniffer(ContentDetectionOptions options) => _options = options;

    public ContentSniffResult? TryDetect(string? fileNameOrPath, ReadOnlySpan<byte> sample)
    {
        if (!_options.EnableEdifact) return null;
        if (sample.IsEmpty) return null;
        int limit = Math.Min(sample.Length, 512);
        int printable = 0;
        for (int j = 0; j < limit; j++)
        {
            byte b = sample[j];
            if (b == '\r' || b == '\n' || (b >= 0x20 && b <= 0x7E)) printable++;
        }
        if (printable < limit * 0.9) return null;
        var slice = sample.Slice(0, Math.Min(1024, sample.Length));
        var text = Encoding.ASCII.GetString(slice);
        int unaIndex = text.IndexOf("UNA");
        bool hasUNA = unaIndex == 0 && text.Length >= 9;
        int unbIndex = text.IndexOf("UNB+");
        if (unbIndex < 0 && !hasUNA) return null;
        int unhCount = 0; int terminators = 0;
        for (int k = 0; k < text.Length; k++)
        {
            if (text[k] == '\'') terminators++;
            if (k + 4 <= text.Length && text.AsSpan(k).StartsWith("UNH+")) unhCount++;
        }
        if (unhCount == 0 && !hasUNA) return null;
        if (terminators < unhCount) return null;
        int confidence = hasUNA ? 95 : 85;
        return new ContentSniffResult("application/edifact", confidence);
    }
}