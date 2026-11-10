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
    public string? OtlpHeaders { get; init; } // comma separated key=value list (OTLP spec format, e.g. "key1=v1,key2=v2")
    public string? OtlpProtocol { get; init; } // "Grpc" (default) or "HttpProtobuf"

    // Tracing
    public double? TracesSampleRatio { get; init; } // null => sample everything; 0..1 => parent-based ratio sampler

    // Route inventory: when set, every enabled source's folders are walked at startup and then on this
    // interval, logging which rule and destination each folder resolves to. Null (default) disables it.
    public TimeSpan? RouteInventoryInterval { get; init; }

    // General resource attributes
    public string? ServiceName { get; init; } // override default
    public string? ServiceVersion { get; init; } // override assembly version
    public string? DeploymentEnvironment { get; init; } // e.g. dev, staging, prod
}
