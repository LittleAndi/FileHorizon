using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;

namespace FileHorizon.Application.Infrastructure.Processing;

/// <summary>
/// Minimal SFTP file content reader (stub). Uses ISftpClientFactory to open read streams and fetch attributes.
/// For now, defaults to anonymous auth when credentials aren't provided. No network tests; use fakes.
/// </summary>
public sealed class SftpFileContentReader : IFileContentReader
{
    private readonly ILogger<SftpFileContentReader> _logger;
    private readonly ISftpClientFactory _factory;
    private readonly IOptionsMonitor<RemoteFileSourcesOptions>? _remoteOptions;
    private readonly ISecretResolver? _secretResolver;

    // Test-friendly minimal constructor
    public SftpFileContentReader(ILogger<SftpFileContentReader> logger, ISftpClientFactory clientFactory)
    {
        _logger = logger;
        _factory = clientFactory;
    }

    // DI constructor: preferred in runtime
    public SftpFileContentReader(
        ILogger<SftpFileContentReader> logger,
        ISftpClientFactory clientFactory,
        IOptionsMonitor<RemoteFileSourcesOptions> remoteOptions,
        ISecretResolver secretResolver)
    {
        _logger = logger;
        _factory = clientFactory;
        _remoteOptions = remoteOptions;
        _secretResolver = secretResolver;
    }

    public async Task<Result<FileAttributesInfo>> GetAttributesAsync(FileReference file, CancellationToken ct)
    {
        if (!string.Equals(file.Scheme, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            return Result<FileAttributesInfo>.Failure(Error.Validation.Invalid($"SftpFileContentReader received non-sftp scheme '{file.Scheme}'"));
        }
        if (!TryResolveEndpoint(file, out var host, out var port, out var remotePath))
        {
            return Result<FileAttributesInfo>.Failure(Error.Validation.Invalid("Invalid SFTP file reference; host/port/path missing"));
        }
        var creds = await ResolveCredentialsAsync(file.SourceName, host, port, ct).ConfigureAwait(false);
        await using var client = _factory.Create(host, port, creds.Username, creds.Password, creds.PrivateKeyPem, creds.PrivateKeyPassphrase);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var attrs = client.GetAttributes(remotePath);
            return Result<FileAttributesInfo>.Success(new FileAttributesInfo(attrs.Size, attrs.LastWriteTimeUtc, null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read attributes over SFTP: {Host}:{Port}{Path}", host, port, remotePath);
            return Result<FileAttributesInfo>.Failure(Error.Unspecified("Sftp.ReadAttributesFailed", ex.Message));
        }
    }

    public async Task<Result<Stream>> OpenReadAsync(FileReference file, CancellationToken ct)
    {
        if (!string.Equals(file.Scheme, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            return Result<Stream>.Failure(Error.Validation.Invalid($"SftpFileContentReader received non-sftp scheme '{file.Scheme}'"));
        }
        if (!TryResolveEndpoint(file, out var host, out var port, out var remotePath))
        {
            return Result<Stream>.Failure(Error.Validation.Invalid("Invalid SFTP file reference; host/port/path missing"));
        }
        var creds = await ResolveCredentialsAsync(file.SourceName, host, port, ct).ConfigureAwait(false);
        // REMOVE await using (must keep client alive for stream lifetime)
        var client = _factory.Create(host, port, creds.Username, creds.Password, creds.PrivateKeyPem, creds.PrivateKeyPassphrase);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var stream = client.OpenRead(remotePath);
            // Client disposed when caller disposes returned stream
            return Result<Stream>.Success(new ClientBoundStream(stream, client));
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning(ex, "Failed to open SFTP stream: {Host}:{Port}{Path}", host, port, remotePath);
            return Result<Stream>.Failure(Error.Unspecified("Sftp.OpenReadFailed", ex.Message));
        }
    }

    private async Task<(string Username, string? Password, string? PrivateKeyPem, string? PrivateKeyPassphrase)>
        ResolveCredentialsAsync(string? sourceName, string host, int port, CancellationToken ct)
    {
        // Defaults for backward compatibility in tests or if options not bound
        if (_remoteOptions is null || _secretResolver is null)
        {
            return ("anonymous", null, null, null);
        }

        var current = _remoteOptions.CurrentValue;
        var sftp = current.Sftp
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(sourceName)
                ? string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase)
                : (string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase) && s.Port == port));

        if (sftp is null)
        {
            // No matching config; fall back to anonymous
            _logger.LogDebug("No SFTP source matched for {Host}:{Port} (sourceName={SourceName}); using anonymous", host, port, sourceName);
            return ("anonymous", null, null, null);
        }

        var username = string.IsNullOrWhiteSpace(sftp.Username) ? "anonymous" : sftp.Username!;
        var password = await _secretResolver.ResolveSecretAsync(sftp.PasswordSecretRef, ct).ConfigureAwait(false);
        var privateKeyPem = await _secretResolver.ResolveSecretAsync(sftp.PrivateKeySecretRef, ct).ConfigureAwait(false);
        var privateKeyPass = await _secretResolver.ResolveSecretAsync(sftp.PrivateKeyPassphraseSecretRef, ct).ConfigureAwait(false);

        return (username, password, privateKeyPem, privateKeyPass);
    }

    private static bool TryResolveEndpoint(FileReference file, out string host, out int port, out string path)
    {
        host = string.Empty; port = 22; path = string.Empty;
        if (!string.IsNullOrWhiteSpace(file.Host) && file.Port is not null)
        {
            host = file.Host!;
            port = file.Port!.Value;
            path = file.Path;
            return true;
        }
        // Attempt to parse identity key form: sftp://host:port/absolute/path
        if (!string.IsNullOrWhiteSpace(file.Path) && file.Path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(file.Path, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
                port = uri.IsDefaultPort ? 22 : uri.Port;
                path = uri.AbsolutePath;
                return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(path);
            }
        }
        return false;
    }

    private sealed class ClientBoundStream(Stream inner, IAsyncDisposable client) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly IAsyncDisposable _client = client;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            try { if (disposing) _inner.Dispose(); } finally { _client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            try { await _inner.DisposeAsync().ConfigureAwait(false); } finally { await _client.DisposeAsync().ConfigureAwait(false); }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
