using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

public sealed class RoutingOptionsValidator : IValidateOptions<RoutingOptions>
{
    public ValidateOptionsResult Validate(string? name, RoutingOptions options)
    {
        if (options is null) return ValidateOptionsResult.Fail("RoutingOptions instance is null");

        var errors = new List<string>();
        for (int i = 0; i < options.Rules.Count; i++)
        {
            var r = options.Rules[i];
            var prefix = $"Routing:rules[{i}]";
            if (string.IsNullOrWhiteSpace(r.Name)) errors.Add($"{prefix}: Name must be specified.");
            if ((r.PathGlob is null) && (r.PathRegex is null) && (r.SourceName is null) && (r.Protocol is null))
                errors.Add($"{prefix}: At least one match criterion (SourceName|Protocol|PathGlob|PathRegex) must be specified.");
            if (r.Destinations.Count == 0) errors.Add($"{prefix}: Destinations must include at least one destination name.");
        }
        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
