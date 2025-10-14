using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

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
            if (!Matches(rule, fileEvent)) continue;
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
            var renamePattern = rule.RenamePattern;
            var targetName = ApplyRename(fileName, renamePattern);
            var writeOptions = new FileWriteOptions(
                Overwrite: rule.Overwrite ?? false,
                ComputeHash: false,
                RenamePattern: renamePattern);

            var plan = new DestinationPlan(destinationName, targetName, writeOptions, kind, isTopic);
            _logger.LogDebug("Router matched rule {Rule} -> {Destination}", rule.Name, destinationName);
            return Task.FromResult(Result<IReadOnlyList<DestinationPlan>>.Success([plan]));
        }

        _logger.LogWarning("No routing rule matched for file {Id} protocol={Protocol} path={Path}", fileEvent.Id, fileEvent.Protocol, fileEvent.Metadata.SourcePath);
        return Task.FromResult(Result<IReadOnlyList<DestinationPlan>>.Failure(Error.Validation.Invalid("No routing rule matched")));
    }

    private DestinationKind ResolveKind(string destinationName)
    {
        if (_destinationsOptions.CurrentValue.Local.Any(l => string.Equals(l.Name, destinationName, StringComparison.OrdinalIgnoreCase))) return DestinationKind.Local;
        if (_destinationsOptions.CurrentValue.Sftp.Any(l => string.Equals(l.Name, destinationName, StringComparison.OrdinalIgnoreCase))) return DestinationKind.Sftp;
        if (_destinationsOptions.CurrentValue.ServiceBus.Any(l => string.Equals(l.Name, destinationName, StringComparison.OrdinalIgnoreCase))) return DestinationKind.ServiceBus;
        return DestinationKind.Local; // default fallback
    }

    private static bool Matches(RoutingRuleOptions r, FileEvent ev)
    {
        // SourceName matching not available on FileEvent yet; reserved for future enhancement.
        if (!string.IsNullOrWhiteSpace(r.Protocol) && !string.Equals(r.Protocol, ev.Protocol, StringComparison.OrdinalIgnoreCase))
            return false;
        var path = ev.Metadata.SourcePath;
        if (!string.IsNullOrWhiteSpace(r.PathGlob) && !GlobMatch(path, r.PathGlob))
            return false;
        if (!string.IsNullOrWhiteSpace(r.PathRegex) && !Regex.IsMatch(path, r.PathRegex!))
            return false;
        return true;
    }

    private static readonly ConcurrentDictionary<string, Matcher> _matcherCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool GlobMatch(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern is "*") return true;
        if (pattern is "*.*") return true;

        // Normalize path
        var normalizedPath = path.Replace('\\', '/');

        // Strip scheme (e.g., sftp://, file://)
        var schemeIdx = normalizedPath.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
            normalizedPath = normalizedPath[(schemeIdx + 3)..];

        // Strip drive letter (C:/)
        if (normalizedPath.Length > 2 && char.IsLetter(normalizedPath[0]) && normalizedPath[1] == ':' && normalizedPath[2] == '/')
            normalizedPath = normalizedPath[3..];

        // Remove leading slash to keep it purely relative
        normalizedPath = normalizedPath.TrimStart('/');

        var matcher = _matcherCache.GetOrAdd(pattern, p =>
        {
            var m = new Matcher(StringComparison.OrdinalIgnoreCase);
            m.AddInclude(p);
            return m;
        });

        var result = matcher.Match(normalizedPath);
        return result.HasMatches;
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
