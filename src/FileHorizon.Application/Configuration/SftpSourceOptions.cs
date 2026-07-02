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
    /// <summary>
    /// Expected server host key fingerprint. Supports OpenSSH SHA256 format ("SHA256:&lt;base64&gt;"),
    /// bare base64 SHA256, or legacy MD5 colon-separated hex. When set, connections to servers whose
    /// host key does not match are rejected.
    /// </summary>
    public string? HostKeyFingerprint { get; set; }
    /// <summary>
    /// If true, require a matching <see cref="HostKeyFingerprint"/> and refuse to connect when none is
    /// configured. If false (default), an unpinned host key is accepted with a warning logged.
    /// </summary>
    public bool StrictHostKey { get; set; } = false;
    public string? DestinationPath { get; set; }
    public bool CreateDestinationDirectories { get; set; } = true;
    /// <summary>
    /// If true, delete the remote file from the SFTP source after it has been successfully transferred to all destinations.
    /// Defaults to false for safety.
    /// </summary>
    public bool DeleteAfterTransfer { get; set; } = false;
    public bool Enabled { get; set; } = true;
}
