using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace FileHorizon.Application.Infrastructure.Remote;

/// <summary>
/// SSH.NET-backed implementation of ISftpClient and factory.
/// </summary>
public sealed class SshNetSftpClientFactory(ILogger<SshNetSftpClientFactory> logger) : FileHorizon.Application.Abstractions.ISftpClientFactory
{
    private readonly ILogger<SshNetSftpClientFactory> _logger = logger;

    public FileHorizon.Application.Abstractions.ISftpClient Create(string host, int port, string username, string? password, string? privateKeyPem, string? privateKeyPassphrase)
    {
        // Build ConnectionInfo
        ConnectionInfo connInfo;
        if (!string.IsNullOrWhiteSpace(privateKeyPem))
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(privateKeyPem));
            PrivateKeyFile pk = string.IsNullOrWhiteSpace(privateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, privateKeyPassphrase);
            var auth = new PrivateKeyAuthenticationMethod(username, pk);
            connInfo = new ConnectionInfo(host, port, username, auth);
        }
        else
        {
            var auth = new PasswordAuthenticationMethod(username, password ?? string.Empty);
            connInfo = new ConnectionInfo(host, port, username, auth);
        }

        var client = new SftpClient(connInfo);
        return new SshNetSftpClientWrapper(_logger, client);
    }

    private sealed class SshNetSftpClientWrapper(ILogger logger, SftpClient client) : FileHorizon.Application.Abstractions.ISftpClient
    {
        private readonly ILogger _logger = logger;
        private readonly SftpClient _client = client;

        public async Task ConnectAsync(CancellationToken ct)
        {
            // SSH.NET is sync; run on background to allow CT cooperative cancellation.
            await Task.Run(() => _client.Connect(), ct).ConfigureAwait(false);
        }

        public (long Size, DateTimeOffset LastWriteTimeUtc) GetAttributes(string path)
        {
            var attrs = _client.GetAttributes(path);
            return (attrs.Size, attrs.LastWriteTimeUtc);
        }

        public Stream OpenRead(string path)
        {
            return _client.OpenRead(path);
        }

        public ValueTask DisposeAsync()
        {
            try { _client.Dispose(); } catch { /* ignore */ }
            return ValueTask.CompletedTask;
        }
    }
}
