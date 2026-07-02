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
    /// <summary>OTLP export is only attempted when explicitly enabled and an endpoint is provided.</summary>
    public static bool IsEnabled(TelemetryOptions options) =>
        options.EnableOtlpExporter && !string.IsNullOrWhiteSpace(options.OtlpEndpoint);

    /// <summary>
    /// Resolves the wire protocol. Defaults to gRPC (collector default on port 4317);
    /// HTTP/Protobuf is selected for values like "HttpProtobuf" / "http/protobuf" / "http".
    /// </summary>
    public static OtlpExportProtocol ResolveProtocol(TelemetryOptions options) =>
        options.OtlpProtocol?.Trim().ToLowerInvariant() switch
        {
            "httpprotobuf" or "http/protobuf" or "http" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc
        };

    /// <summary>Applies endpoint, protocol and optional headers to an exporter options instance.</summary>
    public static void Apply(OtlpExporterOptions exporter, TelemetryOptions options)
    {
        exporter.Endpoint = new Uri(options.OtlpEndpoint!);
        exporter.Protocol = ResolveProtocol(options);
        if (!string.IsNullOrWhiteSpace(options.OtlpHeaders))
        {
            exporter.Headers = options.OtlpHeaders;
        }
    }

    /// <summary>
    /// gRPC over a plaintext <c>http://</c> endpoint requires HTTP/2 without TLS (h2c),
    /// which the .NET runtime rejects unless this AppContext switch is enabled. Call once
    /// during startup when an insecure gRPC endpoint is configured.
    /// </summary>
    public static void EnableInsecureGrpcIfNeeded(TelemetryOptions options)
    {
        if (!IsEnabled(options) || !options.OtlpInsecure)
        {
            return;
        }

        var isGrpc = ResolveProtocol(options) == OtlpExportProtocol.Grpc;
        var isPlaintext = Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp;

        if (isGrpc && isPlaintext)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }
    }
}
