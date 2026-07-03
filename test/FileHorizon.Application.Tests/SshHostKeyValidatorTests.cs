using System.Security.Cryptography;
using System.Text;
using FileHorizon.Application.Infrastructure.Remote;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public sealed class SshHostKeyValidatorTests
{
    private static readonly byte[] HostKey = Encoding.UTF8.GetBytes("fake-ssh-host-key-material");

    private static string Sha256Base64Unpadded() =>
        Convert.ToBase64String(SHA256.HashData(HostKey)).TrimEnd('=');

    private static string Md5ColonHex()
    {
        var hex = Convert.ToHexString(MD5.HashData(HostKey)).ToLowerInvariant();
        return string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }

    [Fact]
    public void Matches_openssh_sha256_format()
    {
        Assert.True(SshHostKeyValidator.FingerprintMatches($"SHA256:{Sha256Base64Unpadded()}", HostKey));
    }

    [Fact]
    public void Matches_bare_base64_sha256()
    {
        Assert.True(SshHostKeyValidator.FingerprintMatches(Sha256Base64Unpadded(), HostKey));
    }

    [Fact]
    public void Matches_padded_base64_sha256()
    {
        Assert.True(SshHostKeyValidator.FingerprintMatches(Convert.ToBase64String(SHA256.HashData(HostKey)), HostKey));
    }

    [Fact]
    public void Matches_legacy_md5_colon_hex()
    {
        Assert.True(SshHostKeyValidator.FingerprintMatches(Md5ColonHex(), HostKey));
        Assert.True(SshHostKeyValidator.FingerprintMatches(Md5ColonHex().ToUpperInvariant(), HostKey));
    }

    [Fact]
    public void Rejects_wrong_fingerprint()
    {
        var other = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("different-key"))).TrimEnd('=');
        Assert.False(SshHostKeyValidator.FingerprintMatches($"SHA256:{other}", HostKey));
        Assert.False(SshHostKeyValidator.FingerprintMatches("16:27:ac:a5:76:28:2d:36:63:1b:56:4d:eb:df:a6:48", HostKey));
    }

    [Fact]
    public void Validate_accepts_matching_fingerprint()
    {
        var ok = SshHostKeyValidator.Validate(NullLogger.Instance, "host", 22, $"SHA256:{Sha256Base64Unpadded()}", strictHostKey: true, "ssh-ed25519", HostKey);
        Assert.True(ok);
    }

    [Fact]
    public void Validate_rejects_mismatched_fingerprint()
    {
        var ok = SshHostKeyValidator.Validate(NullLogger.Instance, "host", 22, "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", strictHostKey: false, "ssh-ed25519", HostKey);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_without_fingerprint_rejects_when_strict()
    {
        var ok = SshHostKeyValidator.Validate(NullLogger.Instance, "host", 22, null, strictHostKey: true, "ssh-ed25519", HostKey);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_without_fingerprint_accepts_when_not_strict()
    {
        var ok = SshHostKeyValidator.Validate(NullLogger.Instance, "host", 22, null, strictHostKey: false, "ssh-ed25519", HostKey);
        Assert.True(ok);
    }
}
