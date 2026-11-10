using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Processing;

public sealed class SimpleFileRouter(
    IOptionsMonitor<RoutingOptions> routingOptions,
    IOptionsMonitor<DestinationsOptions> destinationsOptions,
    ILogger<SimpleFileRouter> logger) : IFileRouter
{
    private readonly IOptionsMonitor<RoutingOptions> _routingOptions = routingOptions;
    private readonly IOptionsMonitor<DestinationsOptions> _destinationsOptions = destinationsOptions;
    private readonly ILogger<SimpleFileRouter> _logger = logger;

    public Task<Result<IReadOnlyList<DestinationPlan>>> RouteAsync(FileEvent fileEvent, CancellationToken ct)
    {
        if (fileEvent is null) return Task.FromResult(Result<IReadOnlyList<DestinationPlan>>.Failure(Error.Validation.NullFileEvent));

        var rules = _routingOptions.CurrentValue.Rules;
        foreach (var rule in rules)
        {
            if (!RouteMatcher.Matches(rule, fileEvent.Protocol, fileEvent.Metadata.SourcePath)) continue;
            if (rule.Destinations.Count == 0) continue;

            // Simple 1:1: pick the first destination
            var destinationName = rule.Destinations[0];
            var kind = ResolveKind(destinationName);
            var isTopic = false;
            if (kind == DestinationKind.ServiceBus)
            {
                var sb = _destinationsOptions.CurrentValue.ServiceBus.FirstOrDefault(x => string.Equals(x.Name, destinationName, StringComparison.OrdinalIgnoreCase));
                if (sb is not null)
                {
                    isTopic = sb.IsTopic;
                }
            }
            var fileName = Path.GetFileName(fileEvent.Metadata.SourcePath);
            // The router is the single owner of RenamePattern: it is applied here to TargetPath and
            // must not be re-applied by sinks (double application expands tokens twice, see #29).
            var targetName = ApplyRename(fileName, rule.RenamePattern);
            var writeOptions = new FileWriteOptions(
                Overwrite: rule.Overwrite ?? false,
                ComputeHash: false);

            var plan = new DestinationPlan(destinationName, targetName, writeOptions, kind, isTopic);
            // Information, and carrying FileId/Path, so one log document links a source file to its rule
            // without joining on TraceId (#68).
            _logger.LogInformation("Router matched rule {Rule} -> {Destination} for file {FileId} path={Path}",
                rule.Name, destinationName, fileEvent.Id, fileEvent.Metadata.SourcePath);
            return Task.FromResult(Result<IReadOnlyList<DestinationPlan>>.Success([plan]));
        }

        _logger.LogWarning("No routing rule matched for file {Id} protocol={Protocol} path={Path}", fileEvent.Id, fileEvent.Protocol, fileEvent.Metadata.SourcePath);
        return Task.FromResult(Result<IReadOnlyList<DestinationPlan>>.Failure(Error.Validation.Invalid("No routing rule matched")));
    }

    private DestinationKind ResolveKind(string destinationName)
        => FindDestinationKind(_destinationsOptions.CurrentValue, destinationName) ?? DestinationKind.Local; // default fallback

    /// <summary>
    /// The kind of the configured destination with this name, or null when no destination has it. Shared
    /// with the startup configuration announcement so both report the same resolution.
    /// </summary>
    internal static DestinationKind? FindDestinationKind(DestinationsOptions destinations, string destinationName)
    {
        if (destinations.Local.Any(l => string.Equals(l.Name, destinationName, StringComparison.OrdinalIgnoreCase))) return DestinationKind.Local;
        if (destinations.Sftp.Any(l => string.Equals(l.Name, destinationName, StringComparison.OrdinalIgnoreCase))) return DestinationKind.Sftp;
        if (destinations.ServiceBus.Any(l => string.Equals(l.Name, destinationName, StringComparison.OrdinalIgnoreCase))) return DestinationKind.ServiceBus;
        if (destinations.AzureBlob.Any(l => string.Equals(l.Name, destinationName, StringComparison.OrdinalIgnoreCase))) return DestinationKind.AzureBlob;
        return null;
    }

    private static string ApplyRename(string fileName, string? renamePattern)
    {
        if (string.IsNullOrWhiteSpace(renamePattern)) return fileName;
        var date = DateTimeOffset.UtcNow;
        return renamePattern
            .Replace("{fileName}", fileName)
            .Replace("{yyyyMMdd}", date.ToString("yyyyMMdd"));
    }
}
