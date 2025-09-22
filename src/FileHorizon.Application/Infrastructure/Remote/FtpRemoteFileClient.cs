using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Remote;

/// <summary>
/// FTP implementation of <see cref="IRemoteFileClient"/> using FluentFTP.
/// Connection lifecycle: created per poll cycle (caller controls lifetime).
/// </summary>
public sealed class FtpRemoteFileClient(ILogger<FtpRemoteFileClient> logger, string host, int port, string? username, string? password, bool passive) : IRemoteFileClient
{
    private readonly ILogger<FtpRemoteFileClient> _logger = logger;
    private readonly string _host = host;
    private readonly int _port = port <= 0 ? 21 : port;
    private readonly string? _username = string.IsNullOrWhiteSpace(username) ? "anonymous" : username;
    private readonly string? _password = password ?? "anonymous@"; // already resolved secret (host layer) when provided
    private readonly bool _passive = passive;
    private AsyncFtpClient? _client;

    public ProtocolType Protocol => ProtocolType.Ftp;
    public string Host => _host;
    public int Port => _port;

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_client != null && _client.IsConnected) return;
        var client = new AsyncFtpClient(_host, _username, _password, _port)
        {
            Config =
            {
                DataConnectionType = _passive ? FtpDataConnectionType.PASV : FtpDataConnectionType.PORT,
                ValidateAnyCertificate = true // TODO: optionally tighten later with configuration
            }
        };
        _client = client;
        try
        {
            await client.Connect(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTP connect failed to {Host}:{Port}", _host, _port);
            throw;
        }
    }

    public async IAsyncEnumerable<IRemoteFileInfo> ListFilesAsync(string path, bool recursive, string pattern, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Client not connected");
        var options = recursive ? FtpListOption.Recursive : FtpListOption.Auto;
        // FluentFTP pattern filtering: we'll post-filter manually for glob style later; for now simple *.* accepted.
        await foreach (var item in _client.GetListingEnumerable(path, options, ct))
        {
            if (ct.IsCancellationRequested) yield break;
            if (item.Type is FtpObjectType.File)
            {
                if (!GlobMatch(item.Name, pattern)) continue;
                yield return new FtpRemoteFileInfo(item.FullName, item.Name, item.Size, item.Modified.ToUniversalTime(), false);
            }
        }
    }

    public async Task<IRemoteFileInfo?> GetFileInfoAsync(string fullPath, CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Client not connected");
        try
        {
            var item = await _client.GetObjectInfo(fullPath, token: ct).ConfigureAwait(false);
            if (item is null || item.Type != FtpObjectType.File) return null;
            return new FtpRemoteFileInfo(item.FullName, item.Name, item.Size, item.Modified.ToUniversalTime(), false);
        }
        catch (FtpCommandException)
        {
            return null; // treat missing
        }
    }

    private static bool GlobMatch(string name, string pattern)
    {
        return string.IsNullOrWhiteSpace(pattern)
            || pattern is "*" or "*.*"
            || (pattern.StartsWith("*.") && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
            || string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            try { await _client.Disconnect(); } catch { /* ignore */ }
            _client.Dispose();
        }
    }

    private sealed record FtpRemoteFileInfo(string FullPath, string Name, long Size, DateTimeOffset LastWriteTimeUtc, bool IsDirectory) : IRemoteFileInfo;
}
