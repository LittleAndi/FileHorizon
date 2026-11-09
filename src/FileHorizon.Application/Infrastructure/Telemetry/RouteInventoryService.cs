using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Telemetry;

/// <summary>
/// Logs, per source folder, which routing rule and destination files arriving there would take, at startup
/// and then every <see cref="TelemetryOptions.RouteInventoryInterval"/> (#68). This covers folders that
/// have had no traffic yet, so a missing folder or one that resolves to nothing is a real gap, not silence.
/// </summary>
/// <remarks>
/// Folders are judged with <see cref="RouteMatcher.MatchFolder"/>, the same rule matching the router uses.
/// A rule that keys on file names is reported as <c>match=pattern-dependent</c>: some files in that folder
/// may route elsewhere or nowhere. Only roles that poll run the inventory, since they own the sources.
/// </remarks>
public sealed class RouteInventoryService(
    Func<ISourceFolderLister> lister,
    IOptions<TelemetryOptions> telemetry,
    IOptions<PipelineOptions> pipeline,
    IOptionsMonitor<RoutingOptions> routing,
    IOptionsMonitor<DestinationsOptions> destinations,
    ILogger<RouteInventoryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (telemetry.Value.RouteInventoryInterval is not { } interval || pipeline.Value.Role is PipelineRole.Worker)
        {
            return;
        }

        logger.LogInformation("Route inventory enabled, interval={Interval}", interval);
        // ExecuteAsync runs synchronously until its first await, and the local walk completes synchronously:
        // yield so a large tree does not hold up host startup.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Route inventory run failed; retrying after {Interval}", interval);
                try
                {
                    await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var activity = Common.Telemetry.TelemetryInstrumentation.ActivitySource.StartActivity("route.inventory", System.Diagnostics.ActivityKind.Internal);

        var rules = routing.CurrentValue.Rules;
        var destinationOptions = destinations.CurrentValue;
        var listings = await lister().ListSourceFoldersAsync(ct).ConfigureAwait(false);

        foreach (var listing in listings)
        {
            if (listing.Error is not null)
            {
                logger.LogWarning("Route inventory could not list source {Source}: {Error}", listing.Source, listing.Error);
                continue;
            }

            var resolved = 0;
            foreach (var folder in listing.Folders)
            {
                if (Resolve(rules, listing, folder) is not { } hit)
                {
                    logger.LogWarning("Route unresolved {Source} folder={Folder}: no rule would match files here", listing.Source, folder);
                    continue;
                }

                resolved++;
                var destination = hit.Rule.Destinations[0];
                var kind = SimpleFileRouter.FindDestinationKind(destinationOptions, destination);
                logger.LogInformation(
                    "Route resolved {Source} folder={Folder} rule={Rule} -> {Destination} kind={Kind} entity={Entity} match={Match}",
                    listing.Source, folder, hit.Rule.Name, destination, kind?.ToString() ?? "Unknown",
                    kind is { } k ? ConfigurationAnnouncer.ResolveEntity(destinationOptions, k, destination) : null,
                    hit.Match is FolderMatch.Exact ? "exact" : "pattern-dependent");
            }

            logger.LogInformation(
                "Route inventory {Source} folders={Folders} resolved={Resolved} unresolved={Unresolved}",
                listing.Source, listing.Folders.Count, resolved, listing.Folders.Count - resolved);
        }
    }

    /// <summary>
    /// The first rule that can take files in this folder, mirroring the router's first-match order. When
    /// that rule is pattern-dependent, files it does not take fall through to later rules.
    /// </summary>
    private static (RoutingRuleOptions Rule, FolderMatch Match)? Resolve(List<RoutingRuleOptions> rules, SourceFolderListing listing, string folder)
    {
        foreach (var rule in rules)
        {
            if (rule.Destinations.Count == 0) continue;
            var match = RouteMatcher.MatchFolder(rule, listing.Protocol, folder, listing.Separator, listing.Pattern);
            if (match != FolderMatch.None) return (rule, match);
        }
        return null;
    }
}
