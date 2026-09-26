using FileHorizon.Application.Common;

namespace FileHorizon.Application.Tests;

public sealed class ProtocolIdentityTests
{
    [Theory]
    [InlineData("/in/plain.txt")]
    [InlineData("/in/my file.txt")]
    [InlineData("/in/order#1.txt")]
    [InlineData("/in/what?.txt")]
    [InlineData("/in/a%20b.txt")]
    [InlineData("/in/50%.txt")]
    [InlineData("/in/åäö.txt")]
    [InlineData("/in/dir with space/x.txt")]
    [InlineData("//home/test/file.txt")]
    public void TryParseRemoteKey_round_trips_the_raw_path(string path)
    {
        var key = ProtocolIdentity.BuildKey(ProtocolType.Sftp, "Host.Example", 2222, path);

        Assert.True(ProtocolIdentity.TryParseRemoteKey(key, out var scheme, out var host, out var port, out var parsedPath));
        Assert.Equal("sftp", scheme);
        Assert.Equal("host.example", host);
        Assert.Equal(2222, port);
        Assert.Equal(path, parsedPath);
    }

    [Fact]
    public void TryParseRemoteKey_reports_zero_port_when_key_has_none()
    {
        var key = ProtocolIdentity.BuildKey(ProtocolType.Ftp, "host", 0, "/in/a b.txt");

        Assert.True(ProtocolIdentity.TryParseRemoteKey(key, out var scheme, out var host, out var port, out var path));
        Assert.Equal("ftp", scheme);
        Assert.Equal("host", host);
        Assert.Equal(0, port);
        Assert.Equal("/in/a b.txt", path);
    }

    [Theory]
    [InlineData("[::1]", 22, "[::1]", 22)]
    [InlineData("[::1]", 0, "[::1]", 0)]
    [InlineData("sftp.example.com", 22, "sftp.example.com", 22)]
    public void TryParseRemoteKey_returns_host_as_built(string configuredHost, int configuredPort, string expectedHost, int expectedPort)
    {
        var key = ProtocolIdentity.BuildKey(ProtocolType.Sftp, configuredHost, configuredPort, "/in/a b.txt");

        Assert.True(ProtocolIdentity.TryParseRemoteKey(key, out _, out var host, out var port, out var path));
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
        Assert.Equal("/in/a b.txt", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/local/path.txt")]
    [InlineData("sftp://host:22")]
    [InlineData("sftp://:22/path")]
    [InlineData("sftp://h:/path")]
    [InlineData("sftp://h:+22/path")]
    [InlineData("sftp://h:0/path")]
    [InlineData("sftp://h:99999999999/path")]
    [InlineData("sftp://h:22relative/path")]
    public void TryParseRemoteKey_rejects_non_remote_keys(string? key)
    {
        Assert.False(ProtocolIdentity.TryParseRemoteKey(key, out _, out _, out _, out _));
    }
}
