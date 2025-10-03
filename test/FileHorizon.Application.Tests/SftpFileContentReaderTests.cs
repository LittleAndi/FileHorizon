using System.Text;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public sealed class SftpFileContentReaderTests
{
    private sealed class FakeSftpClient : ISftpClient
    {
        private readonly byte[] _data;
        private readonly DateTimeOffset _mtime;
        public FakeSftpClient(byte[] data, DateTimeOffset mtime)
        { _data = data; _mtime = mtime; }
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public (long Size, DateTimeOffset LastWriteTimeUtc) GetAttributes(string path) => (_data.Length, _mtime);
        public Stream OpenRead(string path) => new MemoryStream(_data, writable: false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFactory : ISftpClientFactory
    {
        private readonly ISftpClient _client;
        public FakeFactory(ISftpClient client) { _client = client; }
        public ISftpClient Create(string host, int port, string username, string? password, string? privateKeyPem, string? privateKeyPassphrase) => _client;
    }

    [Fact]
    public async Task GetAttributes_succeeds_for_identity_key_path()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var client = new FakeSftpClient(data, mtime);
        var factory = new FakeFactory(client);
        var reader = new SftpFileContentReader(new NullLogger<SftpFileContentReader>(), factory);

        var file = new FileReference("sftp", null, null, "sftp://example:2222//home/test/file.txt", null);
        var res = await reader.GetAttributesAsync(file, CancellationToken.None);
        Assert.True(res.IsSuccess);
        Assert.Equal(data.Length, res.Value!.Size);
    }

    [Fact]
    public async Task OpenRead_returns_stream()
    {
        var data = Encoding.UTF8.GetBytes("abc");
        var client = new FakeSftpClient(data, DateTimeOffset.UtcNow);
        var factory = new FakeFactory(client);
        var reader = new SftpFileContentReader(new NullLogger<SftpFileContentReader>(), factory);
        var file = new FileReference("sftp", null, null, "sftp://h:22//root/a.bin", null);
        var res = await reader.OpenReadAsync(file, CancellationToken.None);
        Assert.True(res.IsSuccess);
        using var s = res.Value!;
        var buf = new byte[3];
        var n = await s.ReadAsync(buf);
        Assert.Equal(3, n);
        Assert.Equal(data, buf);
    }

    [Fact]
    public async Task Rejects_non_sftp_scheme()
    {
        var client = new FakeSftpClient([], DateTimeOffset.UtcNow);
        var factory = new FakeFactory(client);
        var reader = new SftpFileContentReader(new NullLogger<SftpFileContentReader>(), factory);
        var file = new FileReference("local", null, null, "C:/tmp/file.txt", null);
        var res = await reader.OpenReadAsync(file, CancellationToken.None);
        Assert.False(res.IsSuccess);
    }
}
