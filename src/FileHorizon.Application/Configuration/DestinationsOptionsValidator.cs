using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

public sealed class DestinationsOptionsValidator : IValidateOptions<DestinationsOptions>
{
    // Reserved keys that are set by the runtime and cannot be overridden by configuration
    private static readonly HashSet<string> ReservedApplicationPropertyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fh.fileId",
        "fh.protocol",
        "Content-Encoding" // Reserved for compression
    };

    private static readonly HashSet<string> ValidAccessTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hot", "Cool", "Cold", "Archive"
    };

    private static bool IsValidContainerName(string name)
    {
        if (name.Length is < 3 or > 63) return false;
        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0])) return false;
        if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1])) return false;
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') return false;
        }
        return !name.Contains("--");
    }

    public ValidateOptionsResult Validate(string? name, DestinationsOptions options)
    {
        if (options is null) return ValidateOptionsResult.Fail("DestinationsOptions instance is null");

        var errors = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < options.Local.Count; i++)
        {
            var d = options.Local[i];
            var prefix = $"Destinations:local[{i}]";
            if (string.IsNullOrWhiteSpace(d.Name)) errors.Add($"{prefix}: Name must be specified.");
            else if (!seenNames.Add(d.Name)) errors.Add($"{prefix}: Name '{d.Name}' is duplicated.");
            if (string.IsNullOrWhiteSpace(d.RootPath)) errors.Add($"{prefix}: RootPath must be specified.");
            else if (!Common.PathValidator.IsValidLocalPath(d.RootPath, out var lpErr)) errors.Add($"{prefix}: RootPath invalid: {lpErr}");
        }

        for (int i = 0; i < options.Sftp.Count; i++)
        {
            var d = options.Sftp[i];
            var prefix = $"Destinations:sftp[{i}]";
            if (string.IsNullOrWhiteSpace(d.Name)) errors.Add($"{prefix}: Name must be specified.");
            else if (!seenNames.Add(d.Name)) errors.Add($"{prefix}: Name '{d.Name}' is duplicated.");
            if (string.IsNullOrWhiteSpace(d.Host)) errors.Add($"{prefix}: Host must be specified.");
            if (d.Port <= 0 || d.Port > 65535) errors.Add($"{prefix}: Port {d.Port} is out of range (1-65535).");
            if (string.IsNullOrWhiteSpace(d.Username)) errors.Add($"{prefix}: Username must be specified.");
            var hasPass = !string.IsNullOrWhiteSpace(d.PasswordSecretRef);
            var hasKey = !string.IsNullOrWhiteSpace(d.PrivateKeySecretRef);
            if (!hasPass && !hasKey) errors.Add($"{prefix}: Provide either PasswordSecretRef or PrivateKeySecretRef.");
            if (!string.IsNullOrWhiteSpace(d.PrivateKeyPassphraseSecretRef) && !hasKey)
                errors.Add($"{prefix}: PrivateKeyPassphraseSecretRef specified but PrivateKeySecretRef is missing.");
            if (string.IsNullOrWhiteSpace(d.RootPath)) errors.Add($"{prefix}: RootPath must be specified.");
            else if (!Common.PathValidator.IsValidRemotePath(d.RootPath, out var rpErr)) errors.Add($"{prefix}: RootPath invalid: {rpErr}");
        }

        for (int i = 0; i < options.ServiceBus.Count; i++)
        {
            var d = options.ServiceBus[i];
            var prefix = $"Destinations:ServiceBus[{i}]";
            if (string.IsNullOrWhiteSpace(d.Name)) errors.Add($"{prefix}: Name must be specified.");
            else if (!seenNames.Add(d.Name)) errors.Add($"{prefix}: Name '{d.Name}' is duplicated.");
            if (string.IsNullOrWhiteSpace(d.EntityName)) errors.Add($"{prefix}: EntityName must be specified.");
            
            // Validate authentication configuration
            if (d.ServiceBusTechnical is null)
            {
                errors.Add($"{prefix}: ServiceBusTechnical must be configured.");
            }
            else
            {
                var hasConnectionString = !string.IsNullOrWhiteSpace(d.ServiceBusTechnical.ConnectionString);
                var hasNamespace = !string.IsNullOrWhiteSpace(d.ServiceBusTechnical.FullyQualifiedNamespace);
                if (!hasConnectionString && !hasNamespace)
                {
                    errors.Add($"{prefix}: Either ServiceBusTechnical.ConnectionString or ServiceBusTechnical.FullyQualifiedNamespace must be specified.");
                }
            }
            
            // Validate ApplicationProperties for reserved keys
            if (d.ApplicationProperties is not null)
            {
                foreach (var key in d.ApplicationProperties.Keys)
                {
                    if (ReservedApplicationPropertyKeys.Contains(key))
                    {
                        errors.Add($"{prefix}: ApplicationProperty key '{key}' is reserved and cannot be configured.");
                    }
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        errors.Add($"{prefix}: ApplicationProperty keys cannot be null or whitespace.");
                    }
                }
            }
        }

        for (int i = 0; i < options.AzureBlob.Count; i++)
        {
            var d = options.AzureBlob[i];
            var prefix = $"Destinations:AzureBlob[{i}]";
            if (string.IsNullOrWhiteSpace(d.Name)) errors.Add($"{prefix}: Name must be specified.");
            else if (!seenNames.Add(d.Name)) errors.Add($"{prefix}: Name '{d.Name}' is duplicated.");

            if (string.IsNullOrWhiteSpace(d.ContainerName)) errors.Add($"{prefix}: ContainerName must be specified.");
            else if (!IsValidContainerName(d.ContainerName)) errors.Add($"{prefix}: ContainerName '{d.ContainerName}' is invalid (3-63 chars, lowercase letters, digits and hyphens, must start and end with letter or digit).");

            if (!string.IsNullOrWhiteSpace(d.AccessTier) && !ValidAccessTiers.Contains(d.AccessTier))
                errors.Add($"{prefix}: AccessTier '{d.AccessTier}' is invalid. Valid values: Hot, Cool, Cold, Archive.");

            if (d.ContentTypeStrategy == BlobContentTypeStrategy.Provided && string.IsNullOrWhiteSpace(d.ContentType))
                errors.Add($"{prefix}: ContentType must be specified when ContentTypeStrategy is Provided.");

            if (d.BlobTechnical is null)
            {
                errors.Add($"{prefix}: BlobTechnical must be configured.");
            }
            else
            {
                var hasConnectionString = !string.IsNullOrWhiteSpace(d.BlobTechnical.ConnectionString);
                var hasAccount = !string.IsNullOrWhiteSpace(d.BlobTechnical.AccountName);
                var hasServiceUri = !string.IsNullOrWhiteSpace(d.BlobTechnical.ServiceUri);
                if (!hasConnectionString && !hasAccount && !hasServiceUri)
                {
                    errors.Add($"{prefix}: Either BlobTechnical.ConnectionString, BlobTechnical.AccountName or BlobTechnical.ServiceUri must be specified.");
                }
                if (hasServiceUri && !Uri.TryCreate(d.BlobTechnical.ServiceUri, UriKind.Absolute, out _))
                {
                    errors.Add($"{prefix}: BlobTechnical.ServiceUri is not a valid absolute URI.");
                }
                if (d.BlobTechnical.MaxRetries < 0)
                {
                    errors.Add($"{prefix}: BlobTechnical.MaxRetries must be >= 0.");
                }
            }
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
