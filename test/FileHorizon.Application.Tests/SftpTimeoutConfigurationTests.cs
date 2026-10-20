using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Remote;
using Renci.SshNet;
using Xunit;

namespace FileHorizon.Application.Tests;

public class SftpTimeoutConfigurationTests
{
    private static SftpSourceOptions ValidSource() => new()
    {
        Name = "src",
        Host = "sftp.example.com",
        Port = 22,
        RemotePath = "/in",
        Username = "user",
        PasswordSecretRef = "env:PWD"
    };

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(SftpSourceOptions sftp) =>
        new RemoteFileSourcesOptionsValidator().Validate(null, new RemoteFileSourcesOptions { Sftp = [sftp] });

    [Fact]
    public void Defaults_are_finite()
    {
        var timeouts = new SftpSourceOptions().Timeouts();

        Assert.Equal(TimeSpan.FromSeconds(60), timeouts.Operation);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.KeepAlive);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.Connect);
        Assert.Equal(SftpTimeouts.Default, timeouts);
    }

    [Fact]
    public void Timeouts_reflect_configured_seconds()
    {
        var options = new SftpSourceOptions { OperationTimeoutSeconds = 120, KeepAliveIntervalSeconds = 10, ConnectTimeoutSeconds = 5 };

        Assert.Equal(new SftpTimeouts(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)), options.Timeouts());
    }

    [Fact]
    public void Apply_sets_operation_keepalive_and_connect_timeouts_on_the_client()
    {
        using var client = new SftpClient("sftp.example.com", 22, "user", "pwd");

        SshClientTimeouts.Apply(client, SftpTimeouts.FromSeconds(60, 30, 20));

        Assert.Equal(TimeSpan.FromSeconds(60), client.OperationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), client.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), client.ConnectionInfo.Timeout);
    }

    [Fact]
    public void Apply_with_zero_keepalive_disables_keepalives()
    {
        using var client = new SftpClient("sftp.example.com", 22, "user", "pwd");

        SshClientTimeouts.Apply(client, SftpTimeouts.FromSeconds(60, 0, 30));

        Assert.Equal(Timeout.InfiniteTimeSpan, client.KeepAliveInterval);
    }

    [Fact]
    public void Default_source_passes_validation()
    {
        Assert.True(Validate(ValidSource()).Succeeded);
    }

    [Fact]
    public void Zero_keepalive_passes_validation()
    {
        var sftp = ValidSource();
        sftp.KeepAliveIntervalSeconds = 0;

        Assert.True(Validate(sftp).Succeeded);
    }

    [Theory]
    [InlineData(0, 30, 30, "OperationTimeoutSeconds")]
    [InlineData(-1, 30, 30, "OperationTimeoutSeconds")]
    [InlineData(int.MaxValue, 30, 30, "OperationTimeoutSeconds")]
    [InlineData(60, -1, 30, "KeepAliveIntervalSeconds")]
    [InlineData(60, 30, 0, "ConnectTimeoutSeconds")]
    public void Invalid_timeouts_fail_validation(int operation, int keepAlive, int connect, string property)
    {
        var sftp = ValidSource();
        sftp.OperationTimeoutSeconds = operation;
        sftp.KeepAliveIntervalSeconds = keepAlive;
        sftp.ConnectTimeoutSeconds = connect;

        var result = Validate(sftp);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(property));
    }

    [Fact]
    public void Invalid_timeouts_on_disabled_source_still_fail_validation()
    {
        // Downloads of files queued before a source was disabled still use its settings.
        var sftp = ValidSource();
        sftp.Enabled = false;
        sftp.OperationTimeoutSeconds = 0;

        var result = Validate(sftp);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("OperationTimeoutSeconds"));
    }
}
