using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

public sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    private static readonly string[] AllowedProtocols = ["grpc", "httpprotobuf", "http/protobuf"];

    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        if (options is null) return ValidateOptionsResult.Fail("TelemetryOptions instance is null");

        var errors = new List<string>();

        // !(x >= 0 && x <= 1) instead of (x < 0 || x > 1) so NaN is rejected too.
        if (options.TracesSampleRatio is { } ratio && !(ratio >= 0 && ratio <= 1))
        {
            errors.Add($"Telemetry: TracesSampleRatio must be between 0 and 1, but was {ratio}");
        }

        var protocol = options.OtlpProtocol?.Trim();
        if (!string.IsNullOrEmpty(protocol) && !AllowedProtocols.Contains(protocol.ToLowerInvariant()))
        {
            errors.Add($"Telemetry: OtlpProtocol '{options.OtlpProtocol}' is not supported (allowed: Grpc|HttpProtobuf)");
        }

        if (options.EnableOtlpExporter)
        {
            if (string.IsNullOrWhiteSpace(options.OtlpEndpoint))
            {
                errors.Add("Telemetry: OtlpEndpoint must be set when EnableOtlpExporter is true");
            }
            else if (!Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                // Uri.TryCreate alone accepts scheme-less "host:4317" (parsed as scheme "host"),
                // so an explicit http/https allowlist is required to catch a missing scheme.
                errors.Add($"Telemetry: OtlpEndpoint '{options.OtlpEndpoint}' must be an absolute http:// or https:// URI");
            }
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
