namespace FileHorizon.Application.Configuration;

public sealed class DestinationsOptions
{
    public const string SectionName = "Destinations";

    public List<LocalDestinationOptions> Local { get; set; } = [];
    public List<SftpDestinationOptions> Sftp { get; set; } = [];
}

public sealed class LocalDestinationOptions
{
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
}

public sealed class SftpDestinationOptions
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string? PasswordSecretRef { get; set; }
    public string? PrivateKeySecretRef { get; set; }
    public string? PrivateKeyPassphraseSecretRef { get; set; }
    public string RootPath { get; set; } = "/";
    public bool StrictHostKey { get; set; } = false;
}
