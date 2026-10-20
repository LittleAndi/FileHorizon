namespace FileHorizon.Application.Common;

public enum ProtocolType
{
    Local = 0,
    Ftp = 1,
    Sftp = 2
}

public static class ProtocolIdentity
{
    private const string SchemeSeparator = "://";

    public static string BuildKey(ProtocolType protocol, string hostOrEmpty, int portOrZero, string path)
    {
        hostOrEmpty = hostOrEmpty?.Trim().ToLowerInvariant() ?? string.Empty;
        return protocol switch
        {
            ProtocolType.Local => path, // full absolute local path
            _ => $"{protocol.ToString().ToLowerInvariant()}{SchemeSeparator}{hostOrEmpty}{(portOrZero > 0 ? ":" + portOrZero : string.Empty)}{path}"
        };
    }

    /// <summary>
    /// Splits a remote key produced by <see cref="BuildKey"/> back into its parts. The path is returned
    /// verbatim: the key is not a URI, so it must not be parsed with <see cref="Uri"/>, whose
    /// <c>AbsolutePath</c> percent-encodes spaces and non-ASCII and treats <c>#</c>/<c>?</c> as fragment/query.
    /// </summary>
    /// <param name="port">The port from the key, or 0 when the key carries none; callers pick the protocol default.</param>
    public static bool TryParseRemoteKey(string? key, out string scheme, out string host, out int port, out string path)
    {
        scheme = string.Empty; host = string.Empty; port = 0; path = string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return false;

        var schemeEnd = key.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (schemeEnd <= 0) return false;
        var authorityStart = schemeEnd + SchemeSeparator.Length;

        // BuildKey always emits the path with its leading '/', so the first '/' ends the authority.
        var pathStart = key.IndexOf('/', authorityStart);
        if (pathStart < 0) return false;

        // The host is returned exactly as BuildKey wrote it (a bracketed IPv6 host keeps its brackets) so it
        // still matches the configured source Host.
        var authority = key[authorityStart..pathStart];
        var portSeparator = authority.LastIndexOf(':');
        if (portSeparator >= 0 && !authority.EndsWith(']'))
        {
            if (!int.TryParse(authority[(portSeparator + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
            {
                port = 0;
                return false;
            }
            authority = authority[..portSeparator];
        }
        if (authority.Length == 0) return false;

        scheme = key[..schemeEnd];
        host = authority;
        path = key[pathStart..];
        return true;
    }
}
