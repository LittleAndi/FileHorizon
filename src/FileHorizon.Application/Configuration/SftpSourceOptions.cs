namespace FileHorizon.Application.Configuration;

/// <summary>
/// Configuration for a single SFTP source to poll.
/// </summary>
public sealed class SftpSourceOptions
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string RemotePath { get; set; } = "/";
    public string Pattern { get; set; } = "*.*";
    public bool Recursive { get; set; } = true;
    public int MinStableSeconds { get; set; } = 2;
    public string? Username { get; set; }
    public string? PasswordSecretRef { get; set; } // optional if using key auth
    public string? PrivateKeySecretRef { get; set; } // secret reference containing private key material
    public string? PrivateKeyPassphraseSecretRef { get; set; }
    public string? HostKeyFingerprint { get; set; } // optional pinning
    public string? DestinationPath { get; set; }
    public bool CreateDestinationDirectories { get; set; } = true;
    public bool Enabled { get; set; } = true;
}
