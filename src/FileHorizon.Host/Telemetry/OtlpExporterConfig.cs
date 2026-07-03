using FileHorizon.Application.Configuration;
using OpenTelemetry.Exporter;

namespace FileHorizon.Host.Telemetry;

/// <summary>
/// Centralizes mapping from <see cref="TelemetryOptions"/> to the OTLP exporter
/// configuration shared by the traces, metrics and logs pipelines so all three
/// signals are exported identically to the collector.
/// </summary>
internal static class OtlpExporterConfig
{
    // OTLP/HTTP per-signal paths (https://opentelemetry.io/docs/specs/otlp/#otlphttp-request).
    public const string TracesPath = "v1/traces";
    public const string MetricsPath = "v1/metrics";
    public const string LogsPath = "v1/logs";

    /// <summary>OTLP export is only attempted when explicitly enabled and an endpoint is provided.</summary>
    public static bool IsEnabled(TelemetryOptions options) =>
        options.EnableOtlpExporter && !string.IsNullOrWhiteSpace(options.OtlpEndpoint);

    /// <summary>
    /// Resolves the wire protocol. Defaults to gRPC (collector default on port 4317).
    /// Unrecognized values throw; <see cref="TelemetryOptionsValidator"/> reports the
    /// same rule as a friendlier aggregated error at startup.
    /// </summary>
    public static OtlpExportProtocol ResolveProtocol(TelemetryOptions options) =>
        options.OtlpProtocol?.Trim().ToLowerInvariant() switch
        {
            null or "" or "grpc" => OtlpExportProtocol.Grpc,
            "httpprotobuf" or "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
            _ => throw new InvalidOperationException($"Telemetry: OtlpProtocol '{options.OtlpProtocol}' is not supported (allowed: Grpc|HttpProtobuf).")
        };

    /// <summary>
    /// Applies endpoint, protocol and optional headers to an exporter options instance.
    /// Assigning <see cref="OtlpExporterOptions.Endpoint"/> programmatically turns off the
    /// SDK's automatic per-signal path appending, so for HTTP/Protobuf the signal path
    /// (e.g. "v1/traces") is appended here unless the endpoint already includes it.
    /// </summary>
    public static void Apply(OtlpExporterOptions exporter, TelemetryOptions options, string signalPath)
    {
        if (!Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Telemetry OTLP endpoint is invalid: '{options.OtlpEndpoint}' (must be an absolute http:// or https:// URI).");
        }

        var protocol = ResolveProtocol(options);
        exporter.Protocol = protocol;
        exporter.Endpoint = protocol == OtlpExportProtocol.HttpProtobuf
            ? AppendPathIfNotPresent(endpoint, signalPath)
            : endpoint;
        if (!string.IsNullOrWhiteSpace(options.OtlpHeaders))
        {
            exporter.Headers = options.OtlpHeaders;
        }
    }

    private static Uri AppendPathIfNotPresent(Uri endpoint, string signalPath)
    {
        var absolute = endpoint.AbsoluteUri.TrimEnd('/');
        return absolute.EndsWith("/" + signalPath, StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : new Uri(absolute + "/" + signalPath);
    }
}
