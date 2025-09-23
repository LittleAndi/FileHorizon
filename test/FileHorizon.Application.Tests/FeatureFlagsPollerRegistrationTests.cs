using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class FeatureFlagsPollerRegistrationTests
{
    private static ServiceProvider Build(bool enableLocal, bool enableFtp, bool enableSftp)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<PipelineOptions>();
        services.AddOptions<PollingOptions>();
        services.AddOptions<PipelineFeaturesOptions>().Configure(o =>
        {
            o.EnableFileTransfer = true;
            o.EnableLocalPoller = enableLocal;
            o.EnableFtpPoller = enableFtp;
            o.EnableSftpPoller = enableSftp;
        });
        services.AddOptions<RemoteFileSourcesOptions>();
        services.AddSingleton<IValidateOptions<RemoteFileSourcesOptions>, RemoteFileSourcesOptionsValidator>();
        services.AddApplicationServices();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Composite_Includes_Ftp_And_Sftp_When_Enabled()
    {
        var sp = Build(true, true, true);
        var composite = sp.GetRequiredService<MultiProtocolPoller>();
        var field = typeof(MultiProtocolPoller).GetField("_pollers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var value = (IEnumerable<IFilePoller>)field!.GetValue(composite)!;
        Assert.Contains(value, p => p is LocalDirectoryPoller);
        Assert.Contains(value, p => p is FtpPoller);
        Assert.Contains(value, p => p is SftpPoller);
    }

    [Fact]
    public void Composite_Excludes_Disabled_Pollers()
    {
        var sp = Build(true, false, true);
        var composite = sp.GetRequiredService<MultiProtocolPoller>();
        var field = typeof(MultiProtocolPoller).GetField("_pollers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var value = (IEnumerable<IFilePoller>)field!.GetValue(composite)!;
        Assert.Contains(value, p => p is LocalDirectoryPoller);
        Assert.DoesNotContain(value, p => p is FtpPoller);
        Assert.Contains(value, p => p is SftpPoller);

        sp = Build(true, true, false);
        composite = sp.GetRequiredService<MultiProtocolPoller>();
        value = (IEnumerable<IFilePoller>)field!.GetValue(composite)!;
        Assert.Contains(value, p => p is LocalDirectoryPoller);
        Assert.Contains(value, p => p is FtpPoller);
        Assert.DoesNotContain(value, p => p is SftpPoller);
    }

    [Fact]
    public void Composite_Excludes_Local_When_Disabled()
    {
        var sp = Build(false, true, false);
        var composite = sp.GetRequiredService<MultiProtocolPoller>();
        var field = typeof(MultiProtocolPoller).GetField("_pollers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var value = (IEnumerable<IFilePoller>)field!.GetValue(composite)!;
        Assert.DoesNotContain(value, p => p is LocalDirectoryPoller);
        Assert.Contains(value, p => p is FtpPoller);
        Assert.DoesNotContain(value, p => p is SftpPoller);
    }

    [Fact]
    public void Composite_All_Disabled_Yields_Empty()
    {
        var sp = Build(false, false, false);
        var composite = sp.GetRequiredService<MultiProtocolPoller>();
        var field = typeof(MultiProtocolPoller).GetField("_pollers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var value = (IEnumerable<IFilePoller>)field!.GetValue(composite)!;
        Assert.Empty(value);
    }
}
