using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Idempotency;
using FileHorizon.Application.Infrastructure.Polling;
using Microsoft.Extensions.Options;

namespace FileHorizon.Host.Commands;

/// <summary>
/// One-off maintenance command: marks everything currently present on a remote source as already
/// transferred, so adopting a source with an existing backlog does not trigger a bulk transfer.
/// Runs inside the normal host so it uses the same configuration, secrets and remote client as the
/// poller, then exits without starting any background service.
/// </summary>
public static class SeedIdempotencyCommand
{
    public const string FlagName = "--seed-idempotency";

    public static bool IsRequested(string[] args) => HasFlag(args, FlagName);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct)
    {
        var sourceName = GetOption(args, "--source");
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            Console.Error.WriteLine($"Usage: {FlagName} --source <name> [--pattern <glob>] [--dry-run]");
            return 2;
        }

        var dryRun = HasFlag(args, "--dry-run");
        var patternOverride = GetOption(args, "--pattern");

        var remoteSources = services.GetRequiredService<IOptions<RemoteFileSourcesOptions>>().Value;
        var poller = ResolvePoller(services, remoteSources, sourceName, out var resolveError);
        if (poller is null)
        {
            Console.Error.WriteLine(resolveError);
            return 2;
        }

        var store = services.GetRequiredService<IIdempotencyStore>();
        if (!dryRun && store is InMemoryIdempotencyStore)
        {
            Console.Error.WriteLine(
                "No durable idempotency store is configured, so seeded markers would be discarded when this process exits. " +
                "Set Idempotency:DataDirectory (or enable Redis) and try again.");
            return 2;
        }

        var idempotency = services.GetRequiredService<IOptions<IdempotencyOptions>>().Value;
        if (!idempotency.Enabled)
        {
            Console.Error.WriteLine(
                "Warning: Idempotency:Enabled is false, so the pipeline will not consult these markers until it is enabled.");
        }

        var ttl = idempotency.TtlSeconds > 0 ? TimeSpan.FromSeconds(idempotency.TtlSeconds) : (TimeSpan?)null;
        var request = new IdempotencySeedRequest(sourceName!, patternOverride, dryRun, ttl);

        var result = await poller.SeedIdempotencyAsync(request, store, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"Seeding failed: {result.Error}");
            return 1;
        }

        Report(result.Value!, dryRun);
        return 0;
    }

    private static void Report(IdempotencySeedResult seed, bool dryRun)
    {
        Console.WriteLine(dryRun
            ? $"Dry run for source '{seed.SourceName}' (pattern {seed.Pattern}) - nothing was written."
            : $"Seeded source '{seed.SourceName}' (pattern {seed.Pattern}).");
        Console.WriteLine($"  scanned:       {seed.Scanned}");
        if (!dryRun)
        {
            Console.WriteLine($"  marked:        {seed.Marked}");
            Console.WriteLine($"  already known: {seed.AlreadyMarked}");
        }

        if (seed.SampleKeys.Count == 0) return;
        Console.WriteLine($"  sample keys ({seed.SampleKeys.Count} of {seed.Scanned}):");
        foreach (var key in seed.SampleKeys)
        {
            Console.WriteLine($"    {key}");
        }
    }

    /// <summary>
    /// Picks the poller owning the named source. The source name decides the protocol, so a typo
    /// reports the available names rather than silently seeding nothing.
    /// </summary>
    private static RemotePollerBase? ResolvePoller(
        IServiceProvider services,
        RemoteFileSourcesOptions remoteSources,
        string sourceName,
        out string? error)
    {
        error = null;
        var isSftp = remoteSources.Sftp.Any(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        var isFtp = remoteSources.Ftp.Any(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));

        if (!isSftp && !isFtp)
        {
            var known = remoteSources.Sftp.Select(s => s.Name)
                .Concat(remoteSources.Ftp.Select(s => s.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            error = known.Count == 0
                ? $"No remote source named '{sourceName}' is configured (RemoteFileSources has no entries)."
                : $"No remote source named '{sourceName}' is configured. Known sources: {string.Join(", ", known)}.";
            return null;
        }

        try
        {
            return isSftp
                ? services.GetRequiredService<SftpPoller>()
                : services.GetRequiredService<FtpPoller>();
        }
        catch (InvalidOperationException ex)
        {
            // The poller registrations throw when their feature flag is off.
            var flag = isSftp ? "Features:EnableSftpPoller" : "Features:EnableFtpPoller";
            error = $"Cannot seed '{sourceName}': {ex.Message}. Set {flag}=true and try again.";
            return null;
        }
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
