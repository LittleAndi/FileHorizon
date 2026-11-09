using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Telemetry;

/// <summary>
/// Logs one structured event per enabled source and per routing rule at startup, and again whenever that
/// configuration is reloaded, so log consumers can see what this instance polls and how it routes
/// without reading its config (#68).
/// </summary>
/// <remarks>
/// Sources are announced only by roles that poll and rules only by roles that route, so an instance never
/// claims work it does not do. Events carry host, port and path only; never credentials or secret refs.
/// </remarks>
public sealed class ConfigurationAnnouncer(
    IOptionsMonitor<FileSourcesOptions> fileSources,
    IOptionsMonitor<RemoteFileSourcesOptions> remoteSources,
    IOptionsMonitor<RoutingOptions> routing,
    IOptionsMonitor<DestinationsOptions> destinations,
    IOptions<PipelineFeaturesOptions> features,
    IOptions<PipelineOptions> pipeline,
    ILogger<ConfigurationAnnouncer> logger) : IHostedService, IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];

    private bool AnnouncesSources => pipeline.Value.Role is not PipelineRole.Worker;
    private bool AnnouncesRules => pipeline.Value.Role is not PipelineRole.Poller;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (AnnouncesSources)
        {
            AnnounceSources();
            Subscribe(fileSources.OnChange(_ => AnnounceSources()));
            Subscribe(remoteSources.OnChange(_ => AnnounceSources()));
        }
        if (AnnouncesRules)
        {
            AnnounceRules();
            Subscribe(routing.OnChange(_ => AnnounceRules()));
            Subscribe(destinations.OnChange(_ => AnnounceRules()));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }

    private void Subscribe(IDisposable? subscription)
    {
        if (subscription is not null) _subscriptions.Add(subscription);
    }

    internal void AnnounceSources()
    {
        var flags = features.Value;
        if (flags.EnableLocalPoller && TryRead(fileSources, out var local))
        {
            foreach (var s in local.Sources)
            {
                LogSource(s.Name, ProtocolType.Local, ProtocolIdentity.BuildKey(ProtocolType.Local, string.Empty, 0, s.Path), s.Pattern, s.Recursive);
            }
        }
        if ((flags.EnableFtpPoller || flags.EnableSftpPoller) && TryRead(remoteSources, out var remote))
        {
            if (flags.EnableFtpPoller)
            {
                foreach (var s in remote.Ftp.Where(s => s.Enabled))
                {
                    LogSource(s.Name, ProtocolType.Ftp, ProtocolIdentity.BuildKey(ProtocolType.Ftp, s.Host, s.Port, s.RemotePath), s.Pattern, s.Recursive);
                }
            }
            if (flags.EnableSftpPoller)
            {
                foreach (var s in remote.Sftp.Where(s => s.Enabled))
                {
                    LogSource(s.Name, ProtocolType.Sftp, ProtocolIdentity.BuildKey(ProtocolType.Sftp, s.Host, s.Port, s.RemotePath), s.Pattern, s.Recursive);
                }
            }
        }
    }

    private void LogSource(string name, ProtocolType protocol, string root, string pattern, bool recursive)
        => logger.LogInformation("Source configured {Source} protocol={Protocol} root={Root} pattern={Pattern} recursive={Recursive}",
            name, protocol.ToString().ToLowerInvariant(), root, pattern, recursive);

    internal void AnnounceRules()
    {
        if (!TryRead(routing, out var routingOptions) || !TryRead(destinations, out var destinationOptions)) return;

        var rules = routingOptions.Rules;
        if (rules.Count == 0)
        {
            logger.LogWarning("No routing rules configured; every file will fail routing");
            return;
        }

        for (var order = 0; order < rules.Count; order++)
        {
            var rule = rules[order];
            if (rule.Destinations.Count == 0) continue; // rejected by RoutingOptionsValidator

            // Mirrors SimpleFileRouter: first match wins and only the first destination is used.
            var destination = rule.Destinations[0];
            var kind = SimpleFileRouter.FindDestinationKind(destinationOptions, destination);
            logger.LogInformation(
                "Rule configured {Rule} order={Order} protocol={Protocol} glob={PathGlob} regex={PathRegex} source={SourceName} -> {Destination} kind={Kind} entity={Entity}",
                rule.Name, order, rule.Protocol, rule.PathGlob, rule.PathRegex, rule.SourceName, destination,
                kind?.ToString() ?? "Unknown", kind is { } k ? ResolveEntity(destinationOptions, k, destination) : null);

            if (kind is null)
            {
                logger.LogWarning(
                    "Rule {Rule} routes to {Destination}, which is not a configured destination; the router treats it as a Local destination",
                    rule.Name, destination);
            }
            if (rule.Destinations.Count > 1)
            {
                logger.LogWarning(
                    "Rule {Rule} lists {Count} destinations; only the first ({Destination}) is used",
                    rule.Name, rule.Destinations.Count, destination);
            }
            if (!string.IsNullOrWhiteSpace(rule.SourceName))
            {
                logger.LogWarning(
                    "Rule {Rule} sets SourceName={SourceName}, which the router does not enforce yet; the rule matches files from every source",
                    rule.Name, rule.SourceName);
            }
        }
    }

    /// <summary>
    /// Where a destination writes: the Service Bus entity, blob container (and prefix), or root path.
    /// Only called once <see cref="SimpleFileRouter.FindDestinationKind"/> has found the destination.
    /// </summary>
    private static string? ResolveEntity(DestinationsOptions options, DestinationKind kind, string name)
    {
        bool Named(string candidate) => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase);
        switch (kind)
        {
            case DestinationKind.Local:
                return options.Local.First(d => Named(d.Name)).RootPath;
            case DestinationKind.Sftp:
                var sftp = options.Sftp.First(d => Named(d.Name));
                return ProtocolIdentity.BuildKey(ProtocolType.Sftp, sftp.Host, sftp.Port, sftp.RootPath);
            case DestinationKind.ServiceBus:
                return options.ServiceBus.First(d => Named(d.Name)).EntityName;
            case DestinationKind.AzureBlob:
                var blob = options.AzureBlob.First(d => Named(d.Name));
                return string.IsNullOrWhiteSpace(blob.RootPathPrefix)
                    ? blob.ContainerName
                    : $"{blob.ContainerName}/{blob.RootPathPrefix.Trim('/')}";
            default:
                return null;
        }
    }

    /// <summary>
    /// Reads the current options, or logs and returns false when they fail validation. The component that
    /// uses the section reports the failure itself; announcing must not change when or whether that happens.
    /// </summary>
    private bool TryRead<T>(IOptionsMonitor<T> monitor, out T value) where T : class
    {
        try
        {
            value = monitor.CurrentValue;
            return true;
        }
        catch (OptionsValidationException ex)
        {
            logger.LogWarning("Not announcing {Section} configuration: it is invalid ({Failures})", typeof(T).Name, string.Join("; ", ex.Failures));
            value = null!;
            return false;
        }
    }
}
