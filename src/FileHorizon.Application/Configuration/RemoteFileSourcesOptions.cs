namespace FileHorizon.Application.Configuration;

/// <summary>
/// Root options section for remote (FTP/SFTP) file sources.
/// </summary>
public sealed class RemoteFileSourcesOptions
{
    public const string SectionName = "RemoteFileSources";
    public List<FtpSourceOptions> Ftp { get; set; } = new();
    public List<SftpSourceOptions> Sftp { get; set; } = new();
}
