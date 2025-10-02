using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Processing;

/// <summary>
/// Minimal SFTP file content reader (stub). Uses ISftpClientFactory to open read streams and fetch attributes.
/// For now, defaults to anonymous auth when credentials aren't provided. No network tests; use fakes.
/// </summary>
public sealed class SftpFileContentReader(ILogger<SftpFileContentReader> logger, ISftpClientFactory clientFactory) : IFileContentReader
{
    private readonly ILogger<SftpFileContentReader> _logger = logger;
    private readonly ISftpClientFactory _factory = clientFactory;

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
        await using var client = _factory.Create(host, port, username: "anonymous", password: null, privateKeyPem: null, privateKeyPassphrase: null);
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
        await using var client = _factory.Create(host, port, username: "anonymous", password: null, privateKeyPem: null, privateKeyPassphrase: null);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var stream = client.OpenRead(remotePath);
            // Important: we must not dispose client before the consumer reads; wrap stream to dispose client when stream is disposed
            return Result<Stream>.Success(new ClientBoundStream(stream, client));
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning(ex, "Failed to open SFTP stream: {Host}:{Port}{Path}", host, port, remotePath);
            return Result<Stream>.Failure(Error.Unspecified("Sftp.OpenReadFailed", ex.Message));
        }
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
