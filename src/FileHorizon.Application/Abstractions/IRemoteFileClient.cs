using FileHorizon.Application.Common;

namespace FileHorizon.Application.Abstractions;

public interface IRemoteFileClient : IAsyncDisposable
{
    ProtocolType Protocol { get; }
    string Host { get; }
    int Port { get; }
    Task ConnectAsync(CancellationToken ct);
    IAsyncEnumerable<IRemoteFileInfo> ListFilesAsync(string path, bool recursive, string pattern, CancellationToken ct);
    /// <summary>
    /// Like <see cref="ListFilesAsync"/>, but also yields each directory below <paramref name="path"/>
    /// (with <see cref="IRemoteFileInfo.IsDirectory"/> set). Used by the route inventory to find folders
    /// that have no files yet.
    /// </summary>
    IAsyncEnumerable<IRemoteFileInfo> ListTreeAsync(string path, bool recursive, string pattern, CancellationToken ct);
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
