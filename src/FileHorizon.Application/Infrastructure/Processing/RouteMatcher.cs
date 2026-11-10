using FileHorizon.Application.Configuration;
using Microsoft.Extensions.FileSystemGlobbing;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace FileHorizon.Application.Infrastructure.Processing;

/// <summary>How a routing rule applies to the files of a folder, as judged without seeing the files.</summary>
public enum FolderMatch
{
    /// <summary>No file the source picks up in this folder can match the rule.</summary>
    None = 0,
    /// <summary>Every file the source picks up in this folder matches the rule.</summary>
    Exact = 1,
    /// <summary>The rule keys on the file name, so some files in this folder may match it and others not.</summary>
    PatternDependent = 2,
}

/// <summary>
/// Rule matching shared by <see cref="SimpleFileRouter"/> (one concrete file) and the route inventory
/// (a folder, before any file has arrived), so the two can never disagree about what a rule matches.
/// </summary>
public static class RouteMatcher
{
    private const string Probe = "__route_inventory_probe__";

    private static readonly ConcurrentDictionary<string, Matcher> _matcherCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the rule matches a file with this protocol and source path (identity key).</summary>
    public static bool Matches(RoutingRuleOptions rule, string protocol, string path)
    {
        // SourceName matching not available on FileEvent yet; reserved for future enhancement.
        if (!ProtocolMatches(rule, protocol))
            return false;
        if (!string.IsNullOrWhiteSpace(rule.PathGlob) && !GlobMatch(path, rule.PathGlob))
            return false;
        if (!string.IsNullOrWhiteSpace(rule.PathRegex) && !Regex.IsMatch(path, rule.PathRegex!))
            return false;
        return true;
    }

    /// <summary>
    /// How the rule applies to files a source picks up in <paramref name="folder"/> (an identity key, as
    /// the poller would build for a file's directory) using <paramref name="sourcePattern"/>.
    /// </summary>
    /// <remarks>
    /// A glob is split at its last '/': the folder part is matched against the folder, and the file-name
    /// part is compared with the source pattern. A file-name part of <c>*</c>, <c>**</c> or empty (a
    /// trailing '/', which the matcher reads as everything below), one equal to the source pattern, or a
    /// <c>*.ext</c> that the source's <c>*.ext</c> ends with (<c>*.gz</c> for <c>*.tar.gz</c>) takes every
    /// file the source picks up (<see cref="FolderMatch.Exact"/>). Two <c>*.ext</c> forms where neither
    /// extension ends with the other can never overlap (<see cref="FolderMatch.None"/>). Anything else
    /// depends on the actual names.
    /// A regex cannot be judged without a real file name, so a rule with <c>PathRegex</c> is always
    /// pattern-dependent (when its glob, if any, allows the folder). That over-reports regex rules aimed at
    /// other folders, but the inventory never calls a folder unresolved that the router would route.
    /// </remarks>
    public static FolderMatch MatchFolder(RoutingRuleOptions rule, string protocol, string folder, char separator, string sourcePattern)
    {
        if (!ProtocolMatches(rule, protocol))
            return FolderMatch.None;

        var result = FolderMatch.Exact;
        var prefix = folder.TrimEnd('/', '\\') + separator;

        if (!string.IsNullOrWhiteSpace(rule.PathGlob))
        {
            var glob = rule.PathGlob!.Replace('\\', '/');
            var slash = glob.LastIndexOf('/');
            var fileGlob = slash >= 0 ? glob[(slash + 1)..] : glob;

            if (IsMatchAll(glob) || fileGlob is "**" or "")
            {
                // The file-name part takes any name: the probe stands in for every file.
                if (!GlobMatch(prefix + Probe, glob)) return FolderMatch.None;
            }
            else
            {
                // A glob without '/' only matches top-level paths. Testing the probe's own name keeps that
                // (and avoids GlobMatch's match-all shortcut for "*").
                var folderGlob = slash >= 0 ? glob[..slash] + "/*" : Probe;
                if (!GlobMatch(prefix + Probe, folderGlob)) return FolderMatch.None;

                var nameMatch = MatchFileName(fileGlob, sourcePattern);
                if (nameMatch == FolderMatch.None) return FolderMatch.None;
                if (nameMatch == FolderMatch.PatternDependent) result = FolderMatch.PatternDependent;
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.PathRegex))
        {
            result = FolderMatch.PatternDependent;
        }

        return result;
    }

    private static bool ProtocolMatches(RoutingRuleOptions rule, string protocol)
        => string.IsNullOrWhiteSpace(rule.Protocol) || string.Equals(rule.Protocol, protocol, StringComparison.OrdinalIgnoreCase);

    private static bool IsMatchAll(string pattern) => pattern is "*" or "*.*";

    private static FolderMatch MatchFileName(string fileGlob, string sourcePattern)
    {
        if (fileGlob is "*" || string.Equals(fileGlob, sourcePattern, StringComparison.OrdinalIgnoreCase))
            return FolderMatch.Exact;
        if (IsSimpleExtension(fileGlob, out var ruleExt) && IsSimpleExtension(sourcePattern, out var sourceExt))
        {
            // Every *.tar.gz is a *.gz; only some *.gz are *.tar.gz; *.xml and *.csv never meet.
            if (sourceExt.EndsWith("." + ruleExt, StringComparison.OrdinalIgnoreCase)) return FolderMatch.Exact;
            if (!ruleExt.EndsWith("." + sourceExt, StringComparison.OrdinalIgnoreCase)) return FolderMatch.None;
        }
        return FolderMatch.PatternDependent;
    }

    /// <summary>True for a <c>*.ext</c> pattern whose extension has no wildcards.</summary>
    private static bool IsSimpleExtension(string pattern, out string extension)
    {
        extension = pattern.Length > 2 && pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : string.Empty;
        return extension.Length > 0 && extension.IndexOfAny(['*', '?', '[']) < 0;
    }

    internal static bool GlobMatch(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || IsMatchAll(pattern)) return true;

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

        return matcher.Match(normalizedPath).HasMatches;
    }
}
