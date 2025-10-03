namespace FileHorizon.Application.Configuration;

public sealed class PipelineFeaturesOptions
{
    public const string SectionName = "Features";
    public bool EnableFileTransfer { get; set; } = false;
    public bool EnableLocalPoller { get; set; } = true;
    public bool EnableFtpPoller { get; set; } = false;
    public bool EnableSftpPoller { get; set; } = false;
}
