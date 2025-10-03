namespace FileHorizon.Application.Configuration;

/// <summary>
/// Configuration for a single FTP source to poll.
/// </summary>
public sealed class FtpSourceOptions
{
    public string Name { get; set; } = string.Empty; // logical identifier
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string RemotePath { get; set; } = "/"; // starting directory
    public string Pattern { get; set; } = "*.*"; // glob-like pattern applied client-side
    public bool Recursive { get; set; } = true;
    public int MinStableSeconds { get; set; } = 2; // stability window before considering file ready
    public bool Passive { get; set; } = true; // FTP passive mode
    public string? Username { get; set; }
    public string? PasswordSecretRef { get; set; } // Key Vault or external secret reference; resolved in Host layer
    public string? DestinationPath { get; set; } // Optional override destination root
    public bool CreateDestinationDirectories { get; set; } = true;
    /// <summary>
    /// If true, delete the remote file from the FTP source after it has been successfully transferred to all destinations.
    /// Defaults to false for safety.
    /// </summary>
    public bool DeleteAfterTransfer { get; set; } = false;
    public bool Enabled { get; set; } = true; // feature flag per source
}
