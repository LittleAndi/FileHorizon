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
    /// <remarks>
    /// Retained for backwards compatibility and for the common single-key case. To pin several keys
    /// (for example one per host key algorithm, or an outgoing and incoming key during rotation) use
    /// <see cref="HostKeyFingerprints"/>. Both properties may be set; the union is accepted.
    /// </remarks>
    public string? HostKeyFingerprint { get; set; }
    /// <summary>
    /// Additional accepted server host key fingerprints, in the same formats as
    /// <see cref="HostKeyFingerprint"/>. A presented host key is trusted when it matches any entry.
    /// </summary>
    /// <remarks>
    /// Useful when a server offers multiple host key algorithms (e.g. ed25519 and RSA) and the
    /// negotiated one may vary, or to trust both the old and the new key across a rotation window.
    /// </remarks>
    public IList<string> HostKeyFingerprints { get; set; } = [];
    /// <summary>
    /// If true, require a match against the configured fingerprints and refuse to connect when none
    /// are configured. If false (default), an unpinned host key is accepted with a warning logged.
    /// </summary>
    public bool StrictHostKey { get; set; } = false;

    /// <summary>
    /// The union of <see cref="HostKeyFingerprint"/> and <see cref="HostKeyFingerprints"/>,
    /// trimmed and with blank entries removed.
    /// </summary>
    public IReadOnlyList<string> AllHostKeyFingerprints() =>
        HostKeyFingerprintSet.Combine(HostKeyFingerprint, HostKeyFingerprints);
    public string? DestinationPath { get; set; }
    public bool CreateDestinationDirectories { get; set; } = true;
    /// <summary>
    /// If true, delete the remote file from the SFTP source after it has been successfully transferred to all destinations.
    /// Defaults to false for safety.
    /// </summary>
    public bool DeleteAfterTransfer { get; set; } = false;
    public bool Enabled { get; set; } = true;
}
