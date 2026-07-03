namespace FileHorizon.Application.Abstractions;

/// <summary>
/// Abstraction over an SFTP client used by readers to open streams and query attributes.
/// Introduced to allow unit testing without network I/O or SSH.NET dependency in tests.
/// </summary>
public interface ISftpClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Stream OpenRead(string path);
    (long Size, DateTimeOffset LastWriteTimeUtc) GetAttributes(string path);
}

public interface ISftpClientFactory
{
    /// <summary>
    /// Create an SFTP client using either password or private key authentication. One of password or privateKeyPem must be provided.
    /// When <paramref name="hostKeyFingerprint"/> is set, the server host key must match it or the connection is rejected.
    /// When <paramref name="strictHostKey"/> is true and no fingerprint is configured, the connection is rejected.
    /// </summary>
    ISftpClient Create(string host, int port, string username, string? password, string? privateKeyPem, string? privateKeyPassphrase, string? hostKeyFingerprint = null, bool strictHostKey = false);
}
