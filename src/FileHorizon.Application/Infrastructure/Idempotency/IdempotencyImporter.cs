using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Idempotency;

/// <summary>
/// Parameters for importing markers from a JSONL idempotency file into the configured store.
/// </summary>
/// <param name="FilePath">Path to a file written by the file-backed store.</param>
/// <param name="DryRun">When true, the file is parsed and reported on but no markers are written.</param>
public sealed record IdempotencyImportRequest(string FilePath, bool DryRun = false);

/// <summary>
/// Outcome of an import run.
/// </summary>
/// <param name="FilePath">Absolute path of the file that was read.</param>
/// <param name="Lines">Non-blank lines in the file.</param>
/// <param name="Keys">Distinct marker keys those lines describe.</param>
/// <param name="Imported">Markers this run created in the target store (always 0 for a dry run).</param>
/// <param name="AlreadyPresent">Markers the target store already held.</param>
/// <param name="Expired">Markers whose recorded expiry had already passed, so they were dropped.</param>
/// <param name="Unreadable">Lines that could not be parsed and were skipped.</param>
/// <param name="Failed">Markers the target store neither created nor holds - a store that swallowed the write.</param>
/// <param name="SampleKeys">First few keys from the file, for eyeballing before committing to a large run.</param>
public sealed record IdempotencyImportResult(
    string FilePath,
    int Lines,
    int Keys,
    int Imported,
    int AlreadyPresent,
    int Expired,
    int Unreadable,
    int Failed,
    IReadOnlyList<string> SampleKeys);

/// <summary>
/// Copies markers from a file-backed idempotency file into whichever store is configured, so a deployment
/// can change stores - typically file to Redis when a service moves into Kubernetes - without losing the
/// record of what has already been transferred.
/// </summary>
/// <remarks>
/// Marker keys are store-agnostic: <see cref="FileIdentity.BuildIdempotencyKey"/> derives them from the
/// source host, path, size and mtime alone, with nothing about the machine running FileHorizon, so the keys
/// in the file are the keys the pipeline will look up after the move. Only retention is per-store, and each
/// marker keeps the lifetime it had left - no expiry stays permanent, a future expiry is written with the
/// time remaining, and one that has already passed is dropped rather than resurrected.
/// </remarks>
public sealed class IdempotencyImporter
{
    private const int SampleKeyCount = 5;

    private readonly ILogger<IdempotencyImporter> _logger;

    public IdempotencyImporter(ILogger<IdempotencyImporter> logger)
    {
        _logger = logger;
    }

    public async Task<Result<IdempotencyImportResult>> ImportAsync(
        IdempotencyImportRequest request,
        IIdempotencyStore store,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return Result<IdempotencyImportResult>.Failure(
                Error.Validation.Invalid("An idempotency file path is required."));
        }

        var path = Path.GetFullPath(request.FilePath);
        if (!File.Exists(path))
        {
            return Result<IdempotencyImportResult>.Failure(Error.File.NotFound(path));
        }

        IdempotencyFileContents contents;
        try
        {
            contents = IdempotencyFileReader.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IdempotencyImportResult>.Failure(
                Error.Unspecified("Idempotency.ImportFailed", $"Could not read '{path}': {ex.Message}"));
        }

        var samples = contents.Entries.Take(SampleKeyCount).Select(e => e.Key).ToList();
        var now = DateTimeOffset.UtcNow;
        var imported = 0;
        var alreadyPresent = 0;
        var expired = 0;
        var failed = 0;

        try
        {
            foreach (var entry in contents.Entries)
            {
                if (ct.IsCancellationRequested) break;

                TimeSpan? ttl = null;
                if (entry.ExpiresAtUtc is { } expiresAt)
                {
                    var remaining = expiresAt - now;
                    if (remaining <= TimeSpan.Zero)
                    {
                        expired++;
                        continue;
                    }
                    ttl = remaining;
                }

                if (request.DryRun) continue;

                if (await store.TryMarkProcessedAsync(entry.Key, ttl, ct).ConfigureAwait(false))
                {
                    imported++;
                    continue;
                }

                // The stores fail open: a Redis outage makes TryMarkProcessedAsync return false, which is
                // indistinguishable from "already there" - and for a migration that difference is the whole
                // point, since a silent run of nothing-to-do would read as a successful import. Confirming
                // the marker is really present turns a swallowed write into a reported failure.
                if (await store.IsProcessedAsync(entry.Key, ct).ConfigureAwait(false))
                {
                    alreadyPresent++;
                }
                else
                {
                    failed++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Markers written before the failure are durable and correct, and re-importing them is a no-op,
            // so report progress with the error and let the operator simply repeat the run.
            _logger.LogError(ex, "Idempotency import from {Path} failed after importing {Imported} marker(s)", path, imported);
            return Result<IdempotencyImportResult>.Failure(
                Error.Unspecified("Idempotency.ImportFailed", $"Import from '{path}' failed after {imported} marker(s): {ex.Message}"));
        }

        _logger.LogInformation(
            "Idempotency import from {Path} (dryRun={DryRun}): lines={Lines}, keys={Keys}, imported={Imported}, alreadyPresent={AlreadyPresent}, expired={Expired}, unreadable={Unreadable}, failed={Failed}",
            path, request.DryRun, contents.Lines, contents.Entries.Count, imported, alreadyPresent, expired, contents.SkippedLines, failed);

        return Result<IdempotencyImportResult>.Success(new IdempotencyImportResult(
            path,
            contents.Lines,
            contents.Entries.Count,
            imported,
            alreadyPresent,
            expired,
            contents.SkippedLines,
            failed,
            samples));
    }
}
