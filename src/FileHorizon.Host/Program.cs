using FileHorizon.Application;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Common.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;
using OpenTelemetry.Exporter.Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection(PollingOptions.SectionName));
builder.Services.Configure<FileSourcesOptions>(builder.Configuration.GetSection(FileSourcesOptions.SectionName));
builder.Services.Configure<PipelineFeaturesOptions>(builder.Configuration.GetSection(PipelineFeaturesOptions.SectionName));
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));
// Bind pipeline role options
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
// New orchestrator-related options (binding only for now)
builder.Services.Configure<DestinationsOptions>(builder.Configuration.GetSection(DestinationsOptions.SectionName));
builder.Services.Configure<RoutingOptions>(builder.Configuration.GetSection(RoutingOptions.SectionName));
builder.Services.Configure<TransferOptions>(builder.Configuration.GetSection(TransferOptions.SectionName));

builder.Services.AddHealthChecks();

// Bind telemetry options
builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));

var telemetryOptions = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();

// Configure OpenTelemetry (Tracing, Metrics, Logging)
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(
        serviceName: telemetryOptions.ServiceName ?? "FileHorizon",
        serviceVersion: telemetryOptions.ServiceVersion ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        serviceInstanceId: Environment.MachineName)
    .AddAttributes(new[]
    {
        new KeyValuePair<string, object>("deployment.environment", telemetryOptions.DeploymentEnvironment ?? builder.Environment.EnvironmentName)
    });

if (telemetryOptions.EnableLogging)
{
    builder.Logging.ClearProviders(); // Rely solely on OTEL logging
    builder.Logging.AddOpenTelemetry(o =>
    {
        o.IncludeScopes = true;
        o.ParseStateValues = true;
        o.IncludeFormattedMessage = true;
        o.SetResourceBuilder(resourceBuilder);
    });
}

if (builder.Environment.IsDevelopment() ||
    string.Equals(Environment.GetEnvironmentVariable("LOG_CONSOLE_DEV"), "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Logging.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
        o.UseUtcTimestamp = false;
        o.IncludeScopes = false;
    });
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(rb => rb.AddService(telemetryOptions.ServiceName ?? "FileHorizon"))
    .WithMetrics(metrics =>
    {
        if (!telemetryOptions.EnableMetrics) return;
        metrics.SetResourceBuilder(resourceBuilder);
        metrics.AddRuntimeInstrumentation();
        metrics.AddHttpClientInstrumentation();
        metrics.AddMeter(TelemetryInstrumentation.MeterName);
        if (telemetryOptions.EnablePrometheus)
        {
            metrics.AddPrometheusExporter();
        }
        if (telemetryOptions.EnableOtlpExporter && telemetryOptions.OtlpEndpoint is not null)
        {
            metrics.AddOtlpExporter(opt =>
            {
                opt.Endpoint = new Uri(telemetryOptions.OtlpEndpoint);
                if (!string.IsNullOrWhiteSpace(telemetryOptions.OtlpHeaders))
                {
                    opt.Headers = telemetryOptions.OtlpHeaders;
                }
            });
        }
    })
    .WithTracing(tracing =>
    {
        if (!telemetryOptions.EnableTracing) return;
        tracing.SetResourceBuilder(resourceBuilder);
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();
        tracing.AddSource(TelemetryInstrumentation.ActivitySourceName);
        if (telemetryOptions.EnableOtlpExporter && telemetryOptions.OtlpEndpoint is not null)
        {
            tracing.AddOtlpExporter(opt =>
            {
                opt.Endpoint = new Uri(telemetryOptions.OtlpEndpoint);
                if (!string.IsNullOrWhiteSpace(telemetryOptions.OtlpHeaders))
                {
                    opt.Headers = telemetryOptions.OtlpHeaders;
                }
            });
        }
    });

var app = builder.Build();

app.MapHealthChecks("/health");

// Expose Prometheus metrics scraping endpoint if enabled
if (telemetryOptions.EnableMetrics && telemetryOptions.EnablePrometheus)
{
    app.MapPrometheusScrapingEndpoint(); // default '/metrics'
}

app.Run();
