using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Idempotency;
using Microsoft.Extensions.Options;

namespace FileHorizon.Host.Commands;

/// <summary>
/// One-off maintenance command: copies markers from a file-backed idempotency file into the configured
/// store, so a deployment can move between stores - a service moving onto Kubernetes with Redis being the
/// motivating case - without re-transferring everything the old deployment had already fetched.
/// Runs inside the normal host so it writes through the same store selection as the pipeline, then exits
/// without starting any background service.
/// </summary>
public static class ImportIdempotencyCommand
{
    public const string FlagName = "--import-idempotency";

    public static bool IsRequested(string[] args) => CommandLineArgs.HasFlag(args, FlagName);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct)
    {
        var filePath = CommandLineArgs.GetOption(args, FlagName);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            Console.Error.WriteLine($"Usage: {FlagName} <path to idempotency.jsonl> [--dry-run]");
            return 2;
        }

        // Resolved here rather than left to the importer so a path that does not exist is reported as the
        // bad argument it is, alongside the usage error above, instead of as a failed run.
        if (!TryResolvePath(filePath!, out var path, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            return 2;
        }

        var dryRun = CommandLineArgs.HasFlag(args, "--dry-run");
        var store = services.GetRequiredService<IIdempotencyStore>();

        if (!dryRun && store is InMemoryIdempotencyStore)
        {
            Console.Error.WriteLine(IdempotencyStoreRequirement.DescribeUnusable(
                services,
                "Imported markers would be discarded when this process exits, so nothing was written."));
            return 2;
        }

        if (IsOwnFile(store, path!, out var ownFileError))
        {
            Console.Error.WriteLine(ownFileError);
            return 2;
        }

        var idempotency = services.GetRequiredService<IOptions<IdempotencyOptions>>().Value;
        if (!idempotency.Enabled)
        {
            Console.Error.WriteLine(
                "Warning: Idempotency:Enabled is false, so the pipeline will not consult these markers until it is enabled.");
        }

        var importer = services.GetRequiredService<IdempotencyImporter>();
        var result = await importer.ImportAsync(new IdempotencyImportRequest(path!, dryRun), store, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"Import failed: {result.Error}");
            return 1;
        }

        Report(result.Value!, store, dryRun);

        // A swallowed write leaves the target store short of markers, which shows up later as an unwanted
        // re-transfer rather than as an error, so it must not exit 0.
        return result.Value!.Failed > 0 ? 1 : 0;
    }

    private static bool TryResolvePath(string filePath, out string? path, out string? error)
    {
        path = null;
        error = null;
        try
        {
            path = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{filePath}' is not a usable path: {ex.Message}";
            return false;
        }

        if (File.Exists(path)) return true;
        error = $"No idempotency file at {path}.";
        return false;
    }

    /// <summary>
    /// Catches the file-backed store being handed its own file. That import reports every marker as already
    /// present, since the store loaded them at construction, which reads as "nothing to do" when the operator
    /// meant to import the file they copied off the old deployment.
    /// </summary>
    private static bool IsOwnFile(IIdempotencyStore store, string path, out string? error)
    {
        error = null;
        if (store is not FileBackedIdempotencyStore fileStore) return false;

        // Windows paths differ only in case; elsewhere they do not.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(fileStore.FilePath, path, comparison)) return false;

        error = $"{path} is the configured store's own file (Idempotency:DataDirectory), whose markers are " +
                "already loaded. Point --import-idempotency at the file copied from the deployment you are " +
                "migrating from, or configure the store you are importing into.";
        return true;
    }

    private static void Report(IdempotencyImportResult import, IIdempotencyStore store, bool dryRun)
    {
        Console.WriteLine(dryRun
            ? $"Dry run for {import.FilePath} - nothing was written."
            : $"Imported {import.FilePath} into {IdempotencyStoreRequirement.Describe(store)}.");
        Console.WriteLine($"  lines read:    {import.Lines}");
        Console.WriteLine($"  unique keys:   {import.Keys}");
        if (!dryRun)
        {
            Console.WriteLine($"  imported:      {import.Imported}");
            Console.WriteLine($"  already known: {import.AlreadyPresent}");
        }
        if (import.Expired > 0)
        {
            Console.WriteLine($"  expired:       {import.Expired} (dropped)");
        }
        if (import.Unreadable > 0)
        {
            Console.WriteLine($"  unreadable:    {import.Unreadable} line(s) skipped");
        }
        if (import.Failed > 0)
        {
            Console.Error.WriteLine(
                $"  FAILED:        {import.Failed} marker(s) were neither written nor present afterwards. " +
                "The store swallowed the write - check its connectivity and repeat the import.");
        }

        if (import.SampleKeys.Count == 0) return;
        Console.WriteLine($"  sample keys ({import.SampleKeys.Count} of {import.Keys}):");
        foreach (var key in import.SampleKeys)
        {
            Console.WriteLine($"    {key}");
        }
    }
}
