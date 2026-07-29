using System.Security.Cryptography;
using System.Text;
using FileHorizon.Application.Configuration;
using Xunit;

namespace FileHorizon.Application.Tests;

public class HostKeyFingerprintConfigurationTests
{
    private static string Fingerprint(string keyMaterial) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).TrimEnd('=');

    private static readonly string Ed25519 = Fingerprint("ed25519-key");
    private static readonly string Rsa = Fingerprint("rsa-key");

    private static SftpSourceOptions ValidSource() => new()
    {
        Name = "src",
        Host = "sftp.example.com",
        Port = 22,
        RemotePath = "/in",
        Username = "user",
        PasswordSecretRef = "env:PWD"
    };

    private static RemoteFileSourcesOptions Wrap(SftpSourceOptions sftp) => new() { Sftp = [sftp] };

    // --- Combine -------------------------------------------------------------------------

    [Fact]
    public void AllHostKeyFingerprints_is_empty_when_nothing_configured()
    {
        Assert.Empty(new SftpSourceOptions().AllHostKeyFingerprints());
    }

    [Fact]
    public void AllHostKeyFingerprints_returns_singular_value()
    {
        var options = new SftpSourceOptions { HostKeyFingerprint = Ed25519 };
        Assert.Equal([Ed25519], options.AllHostKeyFingerprints());
    }

    [Fact]
    public void AllHostKeyFingerprints_unions_singular_and_plural_with_singular_first()
    {
        var options = new SftpSourceOptions { HostKeyFingerprint = Ed25519, HostKeyFingerprints = [Rsa] };
        Assert.Equal([Ed25519, Rsa], options.AllHostKeyFingerprints());
    }

    [Fact]
    public void AllHostKeyFingerprints_trims_and_drops_blanks_and_duplicates()
    {
        var options = new SftpSourceOptions
        {
            HostKeyFingerprint = $"  {Ed25519}  ",
            HostKeyFingerprints = ["", "   ", Ed25519, Rsa, Rsa]
        };
        Assert.Equal([Ed25519, Rsa], options.AllHostKeyFingerprints());
    }

    [Fact]
    public void AllHostKeyFingerprints_works_when_only_plural_is_set()
    {
        var options = new SftpSourceOptions { HostKeyFingerprints = [Ed25519, Rsa] };
        Assert.Equal([Ed25519, Rsa], options.AllHostKeyFingerprints());
    }

    // --- IsWellFormed --------------------------------------------------------------------

    [Theory]
    [InlineData("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU")]
    [InlineData("sha256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU")]
    [InlineData("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU")]
    [InlineData("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=")]
    [InlineData("16:27:ac:a5:76:28:2d:36:63:1b:56:4d:eb:df:a6:48")]
    [InlineData("16:27:AC:A5:76:28:2D:36:63:1B:56:4D:EB:DF:A6:48")]
    public void IsWellFormed_accepts_supported_formats(string fingerprint)
    {
        Assert.True(HostKeyFingerprintSet.IsWellFormed(fingerprint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-fingerprint")]
    [InlineData("SHA256:tooshort")]
    [InlineData("16:27:ac")]                                        // too few MD5 octets
    [InlineData("zz:27:ac:a5:76:28:2d:36:63:1b:56:4d:eb:df:a6:48")] // non-hex octet
    [InlineData("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU,SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU")]
    public void IsWellFormed_rejects_malformed_values(string fingerprint)
    {
        Assert.False(HostKeyFingerprintSet.IsWellFormed(fingerprint));
    }

    // --- Options validation --------------------------------------------------------------

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(SftpSourceOptions sftp) =>
        new RemoteFileSourcesOptionsValidator().Validate(null, Wrap(sftp));

    [Fact]
    public void Strict_with_only_plural_fingerprints_is_valid()
    {
        var sftp = ValidSource();
        sftp.StrictHostKey = true;
        sftp.HostKeyFingerprints = [Ed25519, Rsa];

        Assert.True(Validate(sftp).Succeeded);
    }

    [Fact]
    public void Strict_without_any_fingerprint_fails()
    {
        var sftp = ValidSource();
        sftp.StrictHostKey = true;

        var result = Validate(sftp);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("StrictHostKey is enabled but no HostKeyFingerprint"));
    }

    [Fact]
    public void Strict_with_only_blank_plural_entries_fails()
    {
        var sftp = ValidSource();
        sftp.StrictHostKey = true;
        sftp.HostKeyFingerprints = ["", "   "];

        Assert.True(Validate(sftp).Failed);
    }

    [Fact]
    public void Malformed_fingerprint_fails_validation()
    {
        var sftp = ValidSource();
        sftp.HostKeyFingerprints = [Ed25519, "obviously-not-a-fingerprint"];

        var result = Validate(sftp);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("obviously-not-a-fingerprint") && f.Contains("not a recognised format"));
    }

    [Fact]
    public void Comma_separated_list_in_singular_property_fails_validation()
    {
        // Operators may reasonably guess that a delimiter works; it must not silently break every connection.
        var sftp = ValidSource();
        sftp.HostKeyFingerprint = $"{Ed25519},{Rsa}";

        Assert.True(Validate(sftp).Failed);
    }

    [Fact]
    public void Well_formed_fingerprints_pass_validation()
    {
        var sftp = ValidSource();
        sftp.HostKeyFingerprint = Ed25519;
        sftp.HostKeyFingerprints = [Rsa, "16:27:ac:a5:76:28:2d:36:63:1b:56:4d:eb:df:a6:48"];

        Assert.True(Validate(sftp).Succeeded);
    }
}
