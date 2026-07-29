using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace FileHorizon.Application.Infrastructure.Remote;

/// <summary>
/// Validates SSH server host keys against one or more operator-configured pinned fingerprints.
/// Supported fingerprint formats: OpenSSH SHA256 ("SHA256:&lt;base64&gt;"), bare base64 SHA256,
/// and legacy MD5 hex pairs separated by colons ("16:27:ac:...").
/// </summary>
public static class SshHostKeyValidator
{
    /// <summary>
    /// Decide whether the presented host key can be trusted.
    /// When any fingerprints are configured the presented key must match at least one of them.
    /// When none are configured, the key is accepted only if <paramref name="strictHostKey"/> is
    /// false (with a warning logged).
    /// </summary>
    public static bool Validate(ILogger logger, string host, int port, IReadOnlyList<string>? expectedFingerprints, bool strictHostKey, string hostKeyName, byte[] hostKey)
    {
        var presented = ToSha256Fingerprint(hostKey);
        var expected = expectedFingerprints?.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray() ?? [];

        if (expected.Length == 0)
        {
            if (strictHostKey)
            {
                logger.LogError("Rejecting SSH host key for {Host}:{Port}: StrictHostKey is enabled but no HostKeyFingerprint is configured. Presented key: {KeyType} {Fingerprint}", host, port, hostKeyName, presented);
                return false;
            }
            logger.LogWarning("SSH host key for {Host}:{Port} is NOT verified (no HostKeyFingerprint configured). Presented key: {KeyType} {Fingerprint}. Configure HostKeyFingerprint to protect against man-in-the-middle attacks", host, port, hostKeyName, presented);
            return true;
        }

        if (AnyFingerprintMatches(expected, hostKey))
        {
            logger.LogDebug("SSH host key for {Host}:{Port} matched a configured fingerprint ({KeyType} {Fingerprint})", host, port, hostKeyName, presented);
            return true;
        }

        logger.LogError("Rejecting SSH host key for {Host}:{Port}: presented key {KeyType} {Fingerprint} does not match any of the {Count} configured host key fingerprint(s)", host, port, hostKeyName, presented, expected.Length);
        return false;
    }

    /// <summary>
    /// True when the host key matches at least one of the supplied fingerprints.
    /// </summary>
    public static bool AnyFingerprintMatches(IEnumerable<string> expectedFingerprints, byte[] hostKey)
    {
        foreach (var expected in expectedFingerprints)
        {
            if (string.IsNullOrWhiteSpace(expected)) continue;
            if (FingerprintMatches(expected, hostKey)) return true;
        }
        return false;
    }

    /// <summary>
    /// Compare a configured fingerprint string against the raw host key bytes.
    /// </summary>
    public static bool FingerprintMatches(string expectedFingerprint, byte[] hostKey)
    {
        var expected = expectedFingerprint.Trim();

        if (expected.Contains(':') && !expected.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            // Legacy MD5 format: colon-separated hex pairs.
            var md5 = Convert.ToHexString(MD5.HashData(hostKey));
            var expectedHex = expected.Replace(":", string.Empty);
            return string.Equals(md5, expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        if (expected.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            expected = expected["SHA256:".Length..];
        }
        // OpenSSH prints SHA256 fingerprints as unpadded base64.
        var sha256 = Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
        return string.Equals(sha256, expected.TrimEnd('='), StringComparison.Ordinal);
    }

    /// <summary>
    /// OpenSSH-style SHA256 fingerprint ("SHA256:&lt;unpadded base64&gt;") of a host key, for logging.
    /// </summary>
    public static string ToSha256Fingerprint(byte[] hostKey)
        => "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
}
