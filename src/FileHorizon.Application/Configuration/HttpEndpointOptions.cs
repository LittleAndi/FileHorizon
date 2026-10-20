namespace FileHorizon.Application.Configuration;

/// <summary>
/// Controls the host's HTTP endpoint (<c>/health</c> and the Prometheus <c>/metrics</c> scrape).
/// Disabling it runs FileHorizon as a plain worker process that binds no port, which suits several
/// instances on one server where assigning a distinct port to each is only overhead.
/// </summary>
public sealed class HttpEndpointOptions
{
    public const string SectionName = "Http";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// True when Prometheus export is configured but cannot be scraped because there is no HTTP endpoint.
    /// </summary>
    public static bool IsPrometheusUnreachable(HttpEndpointOptions http, TelemetryOptions telemetry) =>
        !http.Enabled && telemetry.EnableMetrics && telemetry.EnablePrometheus;
}
