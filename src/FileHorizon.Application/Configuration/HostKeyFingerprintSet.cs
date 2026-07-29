namespace FileHorizon.Application.Configuration;

/// <summary>
/// Helpers for combining the singular and plural host key fingerprint configuration properties
/// into the single list the connection code works with.
/// </summary>
public static class HostKeyFingerprintSet
{
    /// <summary>
    /// Combine a singular fingerprint property with a list property into one de-duplicated list,
    /// trimming whitespace and dropping blank entries. Order is preserved with the singular value first.
    /// </summary>
    public static IReadOnlyList<string> Combine(string? single, IEnumerable<string>? many)
    {
        List<string>? result = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (!seen.Add(trimmed)) return;
            (result ??= []).Add(trimmed);
        }

        Add(single);
        if (many is not null)
        {
            foreach (var value in many) Add(value);
        }

        return (IReadOnlyList<string>?)result ?? [];
    }

    /// <summary>
    /// Shape check for a configured fingerprint, so a typo fails at startup rather than silently
    /// rejecting every connection later. Accepts OpenSSH SHA256 ("SHA256:&lt;base64&gt;"), bare
    /// base64 SHA256, and legacy MD5 colon-separated hex.
    /// </summary>
    public static bool IsWellFormed(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        var value = fingerprint.Trim();

        // Legacy MD5: 16 colon-separated hex pairs.
        if (value.Contains(':') && !value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = value.Split(':');
            return parts.Length == 16 && Array.TrueForAll(parts, p => p.Length == 2 && IsHex(p));
        }

        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }

        // SHA256 is 32 bytes -> 43 unpadded / 44 padded base64 characters.
        var unpadded = value.TrimEnd('=');
        if (unpadded.Length != 43) return false;
        return Convert.TryFromBase64String(unpadded + "=", new byte[32], out var written) && written == 32;
    }

    private static bool IsHex(string pair) =>
        Uri.IsHexDigit(pair[0]) && Uri.IsHexDigit(pair[1]);
}
