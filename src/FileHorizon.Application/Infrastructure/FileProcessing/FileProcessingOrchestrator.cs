using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Common.Telemetry;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace FileHorizon.Application.Infrastructure.FileProcessing;

/// <summary>
/// Orchestrated processor: routes incoming file events to one or more destinations, reads content via protocol-specific reader,
/// and writes via destination sink. Initial implementation handles a single local destination.
/// </summary>
public sealed class FileProcessingOrchestrator(
    IFileRouter router,
    IEnumerable<IFileContentReader> readers,
    IEnumerable<IFileSink> sinks,
    IOptionsMonitor<DestinationsOptions> destinations,
    IOptionsMonitor<IdempotencyOptions> idempotencyOptions,
    Abstractions.IIdempotencyStore idempotencyStore,
    ILogger<FileProcessingOrchestrator> logger) : IFileProcessor
{
    private readonly IFileRouter _router = router;
    private readonly IEnumerable<IFileContentReader> _readers = readers;
    private readonly IEnumerable<IFileSink> _sinks = sinks;
    private readonly IOptionsMonitor<DestinationsOptions> _destinations = destinations;
    private readonly IOptionsMonitor<IdempotencyOptions> _idempotencyOptions = idempotencyOptions;
    private readonly Abstractions.IIdempotencyStore _idempotencyStore = idempotencyStore;
    private readonly ILogger<FileProcessingOrchestrator> _logger = logger;

    public async Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct)
    {
        using var activity = TelemetryInstrumentation.ActivitySource.StartActivity("file.orchestrate", ActivityKind.Internal);
        activity?.SetTag("file.protocol", fileEvent.Protocol);
        activity?.SetTag("file.source_path", fileEvent.Metadata.SourcePath);

        // Idempotency check (optional)
        var idemp = _idempotencyOptions.CurrentValue;
        if (idemp.Enabled)
        {
            var key = $"file:{fileEvent.Id}";
            var ttl = TimeSpan.FromSeconds(Math.Max(1, idemp.TtlSeconds));
            var first = await _idempotencyStore.TryMarkProcessedAsync(key, ttl, ct).ConfigureAwait(false);
            if (!first)
            {
                _logger.LogInformation("Skipping already-processed file event {Id}", fileEvent.Id);
                return Result.Success();
            }
        }

        // Route to destinations
        var route = await _router.RouteAsync(fileEvent, ct).ConfigureAwait(false);
        if (route.IsFailure)
        {
            return Result.Failure(route.Error);
        }
        var plans = route.Value!;
        if (plans.Count == 0)
        {
            return Result.Success(); // nothing to do
        }

        // First cut: handle single destination only
        var plan = plans[0];

        // Resolve destination root from options for local destination type
        var destRoot = ResolveLocalDestinationRoot(plan.DestinationName);
        if (destRoot is null)
        {
            _logger.LogWarning("Unknown destination {Dest}", plan.DestinationName);
            return Result.Failure(Error.Validation.Invalid($"Unknown destination '{plan.DestinationName}'"));
        }

        // Select reader based on protocol
        var reader = SelectReader(fileEvent.Protocol);
        if (reader is null)
        {
            return Result.Failure(Error.Validation.Invalid($"No reader for protocol '{fileEvent.Protocol}'"));
        }

        // For now, we only support local sink
        var sink = _sinks.FirstOrDefault(s => string.Equals(s.Name, "Local", StringComparison.OrdinalIgnoreCase));
        if (sink is null)
        {
            return Result.Failure(Error.Unspecified("Sink.LocalMissing", "Local sink not registered"));
        }

        var sourceRef = new FileReference(
            Scheme: fileEvent.Protocol,
            Host: null,
            Port: null,
            Path: fileEvent.Metadata.SourcePath,
            SourceName: null);

        var targetRef = new FileReference(
            Scheme: "local",
            Host: null,
            Port: null,
            Path: System.IO.Path.Combine(destRoot, plan.TargetPath),
            SourceName: plan.DestinationName);

        var open = await reader.OpenReadAsync(sourceRef, ct).ConfigureAwait(false);
        if (open.IsFailure)
        {
            return Result.Failure(open.Error);
        }

        await using var stream = open.Value!;
        var write = await sink.WriteAsync(targetRef, stream, plan.Options, ct).ConfigureAwait(false);
        if (write.IsFailure)
        {
            return write; // propagate
        }

        return Result.Success();
    }

    private IFileContentReader? SelectReader(string protocol)
        => _readers.FirstOrDefault(r => string.Equals(protocol, "local", StringComparison.OrdinalIgnoreCase) && r is Infrastructure.Processing.LocalFileContentReader);

    private string? ResolveLocalDestinationRoot(string destinationName)
    {
        var d = _destinations.CurrentValue.Local.FirstOrDefault(x => string.Equals(x.Name, destinationName, StringComparison.OrdinalIgnoreCase));
        return d?.RootPath;
    }
}
