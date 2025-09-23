using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

/// <summary>
/// Validates <see cref="RemoteFileSourcesOptions"/> and its nested FTP/SFTP source option entries.
/// Aggregates all failures so startup can surface a single comprehensive error message.
/// </summary>
public sealed class RemoteFileSourcesOptionsValidator : IValidateOptions<RemoteFileSourcesOptions>
{
    public ValidateOptionsResult Validate(string? name, RemoteFileSourcesOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("RemoteFileSourcesOptions instance is null");
        }

        var errors = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ValidateCommon(string prefix, string protocol, string sourceName, string host, int port, string remotePath, string pattern, int minStableSeconds, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                errors.Add($"{prefix}: Name must be specified.");
            }
            else if (!seenNames.Add(sourceName))
            {
                errors.Add($"{prefix}: Name '{sourceName}' is duplicated across remote sources.");
            }

            if (!enabled) return; // skip deeper validation for disabled sources

            if (string.IsNullOrWhiteSpace(host))
            {
                errors.Add($"{prefix}: Host must be specified for enabled source '{sourceName}'.");
            }
            if (port <= 0 || port > 65535)
            {
                errors.Add($"{prefix}: Port {port} is out of range (1-65535).");
            }
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                errors.Add($"{prefix}: RemotePath must be specified.");
            }
            if (string.IsNullOrWhiteSpace(pattern))
            {
                errors.Add($"{prefix}: Pattern must be specified.");
            }
            if (minStableSeconds < 0)
            {
                errors.Add($"{prefix}: MinStableSeconds must be >= 0 (was {minStableSeconds}).");
            }
            else if (minStableSeconds > 3600)
            {
                // Not a fail, but warn that configuration might be unintended. We treat as error to avoid silently huge delays.
                errors.Add($"{prefix}: MinStableSeconds={minStableSeconds} is unusually large (> 3600). Consider lowering.");
            }
        }

        // FTP
        for (int i = 0; i < options.Ftp.Count; i++)
        {
            var ftp = options.Ftp[i];
            var prefix = $"RemoteFileSources:ftp[{i}]";
            ValidateCommon(prefix, "ftp", ftp.Name, ftp.Host, ftp.Port, ftp.RemotePath, ftp.Pattern, ftp.MinStableSeconds, ftp.Enabled);
            if (ftp.Enabled)
            {
                // If a username is provided and not anonymous, require a password secret reference
                if (!string.IsNullOrWhiteSpace(ftp.Username) && !ftp.Username.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(ftp.PasswordSecretRef))
                    {
                        errors.Add($"{prefix}: PasswordSecretRef must be provided for user '{ftp.Username}'.");
                    }
                }
            }
        }

        // SFTP
        for (int i = 0; i < options.Sftp.Count; i++)
        {
            var sftp = options.Sftp[i];
            var prefix = $"RemoteFileSources:sftp[{i}]";
            ValidateCommon(prefix, "sftp", sftp.Name, sftp.Host, sftp.Port, sftp.RemotePath, sftp.Pattern, sftp.MinStableSeconds, sftp.Enabled);
            if (sftp.Enabled)
            {
                if (string.IsNullOrWhiteSpace(sftp.Username))
                {
                    errors.Add($"{prefix}: Username must be specified for enabled SFTP source.");
                }

                // At least one authentication mechanism required
                var hasPassword = !string.IsNullOrWhiteSpace(sftp.PasswordSecretRef);
                var hasKey = !string.IsNullOrWhiteSpace(sftp.PrivateKeySecretRef);
                if (!hasPassword && !hasKey)
                {
                    errors.Add($"{prefix}: Provide either PasswordSecretRef or PrivateKeySecretRef for authentication.");
                }
                if (!string.IsNullOrWhiteSpace(sftp.PrivateKeyPassphraseSecretRef) && !hasKey)
                {
                    errors.Add($"{prefix}: PrivateKeyPassphraseSecretRef specified but PrivateKeySecretRef is missing.");
                }
            }
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
