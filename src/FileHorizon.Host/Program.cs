using FileHorizon.Application;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Common.Telemetry;
using FileHorizon.Host.Commands;
using FileHorizon.Host.Telemetry;
using Microsoft.Extensions.Options;
using NReco.Logging.File;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;
using OpenTelemetry.Exporter.Prometheus;

// Seeding flags are not configuration keys; keep them away from the command-line config provider,
// which rejects bare switches. Configuration still comes from appsettings and the environment.
var seedRequested = SeedIdempotencyCommand.IsRequested(args);
var builder = WebApplication.CreateBuilder(seedRequested ? [] : args);

builder.Services.AddApplicationServices();
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection(PollingOptions.SectionName));
builder.Services.Configure<FileSourcesOptions>(builder.Configuration.GetSection(FileSourcesOptions.SectionName));
builder.Services.Configure<PipelineFeaturesOptions>(builder.Configuration.GetSection(PipelineFeaturesOptions.SectionName));
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.Configure<DestinationsOptions>(builder.Configuration.GetSection(DestinationsOptions.SectionName));
builder.Services.Configure<RoutingOptions>(builder.Configuration.GetSection(RoutingOptions.SectionName));
builder.Services.Configure<RemoteFileSourcesOptions>(builder.Configuration.GetSection(RemoteFileSourcesOptions.SectionName));
builder.Services.Configure<TransferOptions>(builder.Configuration.GetSection(TransferOptions.SectionName));
builder.Services.Configure<IdempotencyOptions>(builder.Configuration.GetSection(IdempotencyOptions.SectionName));

builder.Services.AddHealthChecks();

// Bind telemetry options
builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));

var telemetryOptions = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();

// The telemetry pipelines below are built from this startup snapshot rather than resolved
// IOptions<TelemetryOptions>, so validate it explicitly to fail fast on bad config
// regardless of which telemetry features are enabled.
var telemetryValidation = new TelemetryOptionsValidator().Validate(Options.DefaultName, telemetryOptions);
if (telemetryValidation.Failed)
{
    throw new OptionsValidationException(Options.DefaultName, typeof(TelemetryOptions), telemetryValidation.Failures);
}

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
        if (OtlpExporterConfig.IsEnabled(telemetryOptions))
        {
            o.AddOtlpExporter(exp => OtlpExporterConfig.Apply(exp, telemetryOptions, OtlpExporterConfig.LogsPath));
        }
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

// File logging is opt-in: it activates only when Logging:File:Path is set. Registered after the
// ClearProviders above so it survives the OTEL-only reset, same as the console provider.
var fileLogSection = builder.Configuration.GetSection("Logging:File");
if (!string.IsNullOrWhiteSpace(fileLogSection["Path"]))
{
    builder.Logging.AddFile(fileLogSection);
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
        if (OtlpExporterConfig.IsEnabled(telemetryOptions))
        {
            metrics.AddOtlpExporter(opt => OtlpExporterConfig.Apply(opt, telemetryOptions, OtlpExporterConfig.MetricsPath));
        }
    })
    .WithTracing(tracing =>
    {
        if (!telemetryOptions.EnableTracing) return;
        tracing.SetResourceBuilder(resourceBuilder);
        // Default to sampling everything; a configured ratio applies a parent-based ratio sampler.
        // Range validation happens at startup via TelemetryOptionsValidator.
        if (telemetryOptions.TracesSampleRatio is { } ratio)
        {
            tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)));
        }
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();
        tracing.AddSource(TelemetryInstrumentation.ActivitySourceName);
        if (OtlpExporterConfig.IsEnabled(telemetryOptions))
        {
            tracing.AddOtlpExporter(opt => OtlpExporterConfig.Apply(opt, telemetryOptions, OtlpExporterConfig.TracesPath));
        }
    });

var app = builder.Build();

if (seedRequested)
{
    // Maintenance path: seed idempotency markers and exit without starting any background service.
    int seedExitCode;
    try
    {
        seedExitCode = await SeedIdempotencyCommand.RunAsync(app.Services, args, CancellationToken.None);
    }
    catch (OptionsValidationException ex)
    {
        // Options bound by the command are validated on first resolution. A one-off command should
        // report unusable configuration as the documented exit 2, not as an unhandled crash.
        Console.Error.WriteLine($"Configuration is invalid: {string.Join("; ", ex.Failures)}");
        seedExitCode = 2;
    }
    await app.DisposeAsync();
    return seedExitCode;
}

app.MapHealthChecks("/health");

// Expose Prometheus metrics scraping endpoint if enabled
if (telemetryOptions.EnableMetrics && telemetryOptions.EnablePrometheus)
{
    app.MapPrometheusScrapingEndpoint(); // default '/metrics'
}

app.Run();

return 0;
