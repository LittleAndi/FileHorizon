using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace FileHorizon.Application.Infrastructure.Processing;

public sealed class SimpleFileRouter(IOptionsMonitor<RoutingOptions> options, ILogger<SimpleFileRouter> logger) : IFileRouter
{
    private readonly IOptionsMonitor<RoutingOptions> _options = options;
    private readonly ILogger<SimpleFileRouter> _logger = logger;

    public Task<Result<IReadOnlyList<DestinationPlan>>> RouteAsync(FileEvent fileEvent, CancellationToken ct)
    {
        if (fileEvent is null) return Task.FromResult(Result<IReadOnlyList<DestinationPlan>>.Failure(Error.Validation.NullFileEvent));

        var rules = _options.CurrentValue.Rules;
        foreach (var rule in rules)
        {
            if (!Matches(rule, fileEvent)) continue;
            if (rule.Destinations.Count == 0) continue;

            // Simple 1:1: pick the first destination
            var destinationName = rule.Destinations[0];
            var fileName = Path.GetFileName(fileEvent.Metadata.SourcePath);
            var renamePattern = rule.RenamePattern;
            var targetName = ApplyRename(fileName, renamePattern);
            var writeOptions = new FileWriteOptions(
                Overwrite: rule.Overwrite ?? false,
                ComputeHash: false,
                RenamePattern: renamePattern);

            var plan = new DestinationPlan(destinationName, targetName, writeOptions);
            _logger.LogDebug("Router matched rule {Rule} -> {Destination}", rule.Name, destinationName);
            return Task.FromResult(Result<IReadOnlyList<DestinationPlan>>.Success([plan]));
        }

        _logger.LogWarning("No routing rule matched for file {Id} protocol={Protocol} path={Path}", fileEvent.Id, fileEvent.Protocol, fileEvent.Metadata.SourcePath);
        return Task.FromResult(Result<IReadOnlyList<DestinationPlan>>.Failure(Error.Validation.Invalid("No routing rule matched")));
    }

    private static bool Matches(RoutingRuleOptions r, FileEvent ev)
    {
        // SourceName matching not available on FileEvent yet; reserved for future enhancement.
        if (!string.IsNullOrWhiteSpace(r.Protocol) && !string.Equals(r.Protocol, ev.Protocol, StringComparison.OrdinalIgnoreCase))
            return false;
        var path = ev.Metadata.SourcePath;
        if (!string.IsNullOrWhiteSpace(r.PathGlob) && !GlobMatch(r.PathGlob!, path))
            return false;
        if (!string.IsNullOrWhiteSpace(r.PathRegex) && !Regex.IsMatch(path, r.PathRegex!))
            return false;
        return true;
    }

    private static bool GlobMatch(string pattern, string text)
    {
        // Normalize Windows paths to forward slashes for matching
        var normalized = text.Replace('\\', '/');
        // Very small glob support: ** and * only
        var regex = Regex.Escape(pattern)
            .Replace("\\*\\*", ".*") // ** -> .*
            .Replace("\\*", "[^/]*")   // * -> any except path separator
            .Replace("\\?", ".");     // ? -> single char
        var re = new Regex("^" + regex + "$", RegexOptions.IgnoreCase);
        return re.IsMatch(normalized);
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
