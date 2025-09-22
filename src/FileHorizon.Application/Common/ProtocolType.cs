namespace FileHorizon.Application.Common;

public enum ProtocolType
{
    Local = 0,
    Ftp = 1,
    Sftp = 2
}

public static class ProtocolIdentity
{
    public static string BuildKey(ProtocolType protocol, string hostOrEmpty, int portOrZero, string path)
    {
        hostOrEmpty = hostOrEmpty?.Trim().ToLowerInvariant() ?? string.Empty;
        path = NormalizePath(path);
        return protocol switch
        {
            ProtocolType.Local => path, // full absolute local path
            _ => $"{protocol.ToString().ToLowerInvariant()}://{hostOrEmpty}{(portOrZero > 0 ? ":" + portOrZero : string.Empty)}{path}"
        };
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        path = path.Replace("\\", "/");
        if (!path.StartsWith('/')) path = "/" + path;
        return path;
    }
}
