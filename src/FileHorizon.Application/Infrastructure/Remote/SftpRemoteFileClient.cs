using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileSystemGlobbing;
using System.Collections.Concurrent;
using Renci.SshNet;

namespace FileHorizon.Application.Infrastructure.Remote;

/// <summary>
/// SFTP implementation of <see cref="IRemoteFileClient"/> using SSH.NET.
/// Note: SSH.NET lacks native cancellation for many operations; cooperative cancellation is applied where feasible.
/// </summary>
public sealed class SftpRemoteFileClient : IRemoteFileClient
{
    private readonly ILogger<SftpRemoteFileClient> _logger;
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string? _password;
    private readonly string? _privateKeyPem;
    private readonly string? _privateKeyPassphrase;
    private SftpClient? _client;

    public SftpRemoteFileClient(
        ILogger<SftpRemoteFileClient> logger,
        string host,
        int port,
        string username,
        string? password,
        string? privateKeyPem,
        string? privateKeyPassphrase)
    {
        _logger = logger;
        _host = host;
        _port = port <= 0 ? 22 : port;
        _username = username;
        _password = password;
        _privateKeyPem = privateKeyPem;
        _privateKeyPassphrase = privateKeyPassphrase;
    }

    public ProtocolType Protocol => ProtocolType.Sftp;
    public string Host => _host;
    public int Port => _port;

    public Task ConnectAsync(CancellationToken ct)
    {
        if (_client != null && _client.IsConnected) return Task.CompletedTask;

        _client = CreateClient();
        try
        {
            _client.Connect();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SFTP connect failed to {Host}:{Port}", _host, _port);
            throw;
        }
        return Task.CompletedTask;
    }

    private SftpClient CreateClient()
    {
        if (!string.IsNullOrWhiteSpace(_privateKeyPem))
        {
            using var keyStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(_privateKeyPem));
            PrivateKeyFile pkFile = string.IsNullOrWhiteSpace(_privateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, _privateKeyPassphrase);
            return new SftpClient(_host, _port, _username, new PrivateKeyFile[] { pkFile });
        }
        return new SftpClient(_host, _port, _username, _password ?? string.Empty);
    }

    public async IAsyncEnumerable<IRemoteFileInfo> ListFilesAsync(string path, bool recursive, string pattern, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Client not connected");
        foreach (var entry in _client.ListDirectory(path))
        {
            if (ct.IsCancellationRequested) yield break;
            if (entry.IsDirectory)
            {
                if (recursive && !IsDotDir(entry.Name))
                {
                    await foreach (var child in ListFilesAsync(entry.FullName, true, pattern, ct))
                    {
                        yield return child;
                    }
                }
                continue;
            }
            if (!GlobMatch(entry.Name, pattern)) continue;
            yield return new SftpRemoteFileInfo(entry.FullName, entry.Name, entry.Attributes.Size, entry.LastWriteTimeUtc, false);
        }
    }

    public Task<IRemoteFileInfo?> GetFileInfoAsync(string fullPath, CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Client not connected");
        try
        {
            var attrs = _client.GetAttributes(fullPath);
            if (attrs.IsDirectory) return Task.FromResult<IRemoteFileInfo?>(null);
            var name = System.IO.Path.GetFileName(fullPath);
            IRemoteFileInfo info = new SftpRemoteFileInfo(fullPath, name, attrs.Size, attrs.LastWriteTimeUtc, false);
            return Task.FromResult<IRemoteFileInfo?>(info);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return Task.FromResult<IRemoteFileInfo?>(null);
        }
    }

    public Task DeleteAsync(string fullPath, CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Client not connected");
        try
        {
            _client.DeleteFile(fullPath);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            // ignore missing
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete SFTP file {Path}", fullPath);
        }
        return Task.CompletedTask;
    }

    private static bool IsDotDir(string name) => name == "." || name == "..";

    // Cache compiled matchers for patterns to avoid re-parsing on every file.
    private static readonly ConcurrentDictionary<string, Matcher> _matcherCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool GlobMatch(string name, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern is "*") return true; // treat *.* as historical match-all too
        if (pattern is "*.*") return true;

        // We only match against the file name here (not directory path).
        // FileSystemGlobbing expects patterns relative to a root; we'll feed the name as a single-segment path.
        var matcher = _matcherCache.GetOrAdd(pattern, p =>
        {
            var m = new Matcher(StringComparison.OrdinalIgnoreCase);
            m.AddInclude(p);
            return m;
        });

        // Matcher works over directory structures. We'll emulate a minimal structure by executing against a single in-memory file list.
        // Simpler approach: matcher.Match(name) which is an overload added in .NET 8 package for direct string matching is not available; emulate manually.
        // Use PatternMatchingResult directly via Match(string) extension if available; otherwise, fallback to creating a virtual segment.
        var result = matcher.Match(name);
        return result.HasMatches;
    }

    public ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            try { _client.Dispose(); } catch { }
        }
        return ValueTask.CompletedTask;
    }

    private sealed record SftpRemoteFileInfo(string FullPath, string Name, long Size, DateTimeOffset LastWriteTimeUtc, bool IsDirectory) : IRemoteFileInfo;
}
