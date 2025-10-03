using FileHorizon.Application.Configuration;
using Xunit;

namespace FileHorizon.Application.Tests;

/// <summary>
/// Placeholder tests for future remote delete-after-transfer behavior.
/// Current implementation wires deletion inside FileProcessingOrchestrator which depends on multiple services.
/// A focused integration test harness will be added later.
/// </summary>
public class RemoteDeletionTests
{
    [Fact(Skip = "Orchestrator integration test pending - requires building a minimal DI container")]
    public void DeleteAfterTransfer_flag_present_on_options_defaults_false()
    {
        var ftp = new FtpSourceOptions();
        var sftp = new SftpSourceOptions();
        Assert.False(ftp.DeleteAfterTransfer);
        Assert.False(sftp.DeleteAfterTransfer);
    }
}
