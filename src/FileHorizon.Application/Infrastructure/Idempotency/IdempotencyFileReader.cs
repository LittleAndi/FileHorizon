using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileHorizon.Application.Infrastructure.Idempotency;

/// <summary>
/// One line of the JSONL file written by <see cref="FileBackedIdempotencyStore"/>: a marker key and the
/// instant it expires, null meaning it never does.
/// </summary>
internal sealed record IdempotencyFileEntry(
    [property: JsonPropertyName("k")] string Key,
    [property: JsonPropertyName("e")] DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// What one pass over an idempotency file produced.
/// </summary>
/// <param name="Entries">Distinct keys in the order first seen, each carrying its last recorded expiry.</param>
/// <param name="Lines">Non-blank lines read.</param>
/// <param name="SkippedLines">Lines that could not be read as an entry.</param>
internal sealed record IdempotencyFileContents(
    IReadOnlyList<IdempotencyFileEntry> Entries,
    int Lines,
    int SkippedLines);

/// <summary>
/// Reads the JSONL idempotency file. Shared by <see cref="FileBackedIdempotencyStore"/>'s load path and
/// <see cref="IdempotencyImporter"/>, so a file cannot mean one thing to the store that wrote it and
/// something else to the import that moves those markers into another store.
/// </summary>
internal static class IdempotencyFileReader
{
    /// <summary>
    /// Reads every entry from <paramref name="filePath"/>, which must exist. Unreadable lines are counted
    /// rather than thrown: a torn final line is the normal result of a crash mid-append, and discarding one
    /// marker only costs a single re-transfer, while refusing the whole file would cost the entire history.
    /// </summary>
    public static IdempotencyFileContents Read(string filePath)
    {
        var expiryByKey = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        var order = new List<string>();
        var lines = 0;
        var skipped = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lines++;

            IdempotencyFileEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<IdempotencyFileEntry>(line);
            }
            catch (JsonException)
            {
                skipped++;
                continue;
            }
            if (entry?.Key is null)
            {
                skipped++;
                continue;
            }

            // Last write wins for a key that appears more than once; the key keeps the position where it
            // first appeared so callers report keys in file order.
            if (!expiryByKey.ContainsKey(entry.Key)) order.Add(entry.Key);
            expiryByKey[entry.Key] = entry.ExpiresAtUtc;
        }

        var entries = order.Select(k => new IdempotencyFileEntry(k, expiryByKey[k])).ToList();
        return new IdempotencyFileContents(entries, lines, skipped);
    }
}
