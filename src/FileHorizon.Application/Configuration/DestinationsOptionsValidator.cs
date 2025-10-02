using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

public sealed class DestinationsOptionsValidator : IValidateOptions<DestinationsOptions>
{
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
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
