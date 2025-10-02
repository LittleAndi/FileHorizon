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
    /// </summary>
    ISftpClient Create(string host, int port, string username, string? password, string? privateKeyPem, string? privateKeyPassphrase);
}
