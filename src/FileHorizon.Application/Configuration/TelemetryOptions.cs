namespace FileHorizon.Application.Configuration;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public bool EnableTracing { get; init; } = true;
    public bool EnableMetrics { get; init; } = true;
    public bool EnableLogging { get; init; } = true; // Structured OTEL logging
    public bool EnablePrometheus { get; init; } = true;
    public bool EnableOtlpExporter { get; init; } = false;

    // OTLP specific
    public string? OtlpEndpoint { get; init; }
    public string? OtlpHeaders { get; init; } // semicolon or comma separated key=value list
    public bool OtlpInsecure { get; init; } = false;

    // General resource attributes
    public string? ServiceName { get; init; } // override default
    public string? ServiceVersion { get; init; } // override assembly version
    public string? DeploymentEnvironment { get; init; } // e.g. dev, staging, prod
}
