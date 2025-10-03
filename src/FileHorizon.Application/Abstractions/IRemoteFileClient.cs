using FileHorizon.Application.Common;

namespace FileHorizon.Application.Abstractions;

public interface IRemoteFileClient : IAsyncDisposable
{
    ProtocolType Protocol { get; }
    string Host { get; }
    int Port { get; }
    Task ConnectAsync(CancellationToken ct);
    IAsyncEnumerable<IRemoteFileInfo> ListFilesAsync(string path, bool recursive, string pattern, CancellationToken ct);
    Task<IRemoteFileInfo?> GetFileInfoAsync(string fullPath, CancellationToken ct);
    /// <summary>
    /// Delete the remote file at the given full path. Should not throw if the file does not exist.
    /// </summary>
    Task DeleteAsync(string fullPath, CancellationToken ct);
}

public interface IRemoteFileInfo
{
    string FullPath { get; }
    string Name { get; }
    long Size { get; }
    DateTimeOffset LastWriteTimeUtc { get; }
    bool IsDirectory { get; }
}
