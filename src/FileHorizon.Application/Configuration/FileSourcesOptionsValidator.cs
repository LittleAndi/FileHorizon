using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

public sealed class FileSourcesOptionsValidator : IValidateOptions<FileSourcesOptions>
{
    public ValidateOptionsResult Validate(string? name, FileSourcesOptions options)
    {
        if (options is null) return ValidateOptionsResult.Fail("FileSourcesOptions instance is null");

        var errors = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < options.Sources.Count; i++)
        {
            var s = options.Sources[i];
            var prefix = $"FileSources:sources[{i}]";
            if (string.IsNullOrWhiteSpace(s.Name)) errors.Add($"{prefix}: Name must be specified.");
            else if (!seenNames.Add(s.Name)) errors.Add($"{prefix}: Name '{s.Name}' is duplicated.");
            if (string.IsNullOrWhiteSpace(s.Path)) errors.Add($"{prefix}: Path must be specified.");
            else if (!Common.PathValidator.IsValidLocalPath(s.Path, out var lpErr)) errors.Add($"{prefix}: Path invalid: {lpErr}");
            if (!string.IsNullOrWhiteSpace(s.DestinationPath) && !Common.PathValidator.IsValidLocalPath(s.DestinationPath, out var dpErr))
                errors.Add($"{prefix}: DestinationPath invalid: {dpErr}");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
