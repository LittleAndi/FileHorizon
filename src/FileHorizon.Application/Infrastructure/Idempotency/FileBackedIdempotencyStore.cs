using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileHorizon.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Idempotency;

/// <summary>
/// Durable idempotency store for deployments without Redis. Markers are kept in memory and
/// appended to a JSONL file (one entry per line) so they survive restarts. Designed for a
/// single process; appends are serialized, reads are lock-free against the in-memory map.
/// With indefinite retention (null ttl) every line stays valid, so no compaction is performed.
/// </summary>
public sealed class FileBackedIdempotencyStore : IIdempotencyStore, IDisposable
{
    public const string DefaultFileName = "idempotency.jsonl";

    private readonly ConcurrentDictionary<string, DateTimeOffset?> _entries = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StreamWriter _writer;
    private readonly ILogger<FileBackedIdempotencyStore> _logger;

    public FileBackedIdempotencyStore(string filePath, ILogger<FileBackedIdempotencyStore> logger)
    {
        _logger = logger;
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LoadExistingEntries(filePath);

        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream);
    }

    public Task<bool> IsProcessedAsync(string key, CancellationToken ct)
    {
        return Task.FromResult(IsActive(key));
    }

    public async Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        var expiry = ttl is { } t ? DateTimeOffset.UtcNow + t : (DateTimeOffset?)null;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsActive(key))
            {
                return false;
            }
            _entries[key] = expiry;
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new Entry(key, expiry))).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _writeLock.Dispose();
    }

    private bool IsActive(string key)
    {
        return _entries.TryGetValue(key, out var expiry)
            && (expiry is null || expiry > DateTimeOffset.UtcNow);
    }

    private void LoadExistingEntries(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var loaded = 0;
        var skipped = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<Entry>(line);
                if (entry?.Key is null)
                {
                    skipped++;
                    continue;
                }
                // Last write wins for a key that appears multiple times.
                _entries[entry.Key] = entry.ExpiresAtUtc;
                loaded++;
            }
            catch (JsonException)
            {
                // A torn final line after a crash is expected; skip it.
                skipped++;
            }
        }

        if (skipped > 0)
        {
            _logger.LogWarning("Idempotency file {Path}: skipped {Skipped} unreadable line(s) while loading {Loaded} marker(s)", filePath, skipped, loaded);
        }
        else
        {
            _logger.LogInformation("Idempotency file {Path}: loaded {Loaded} marker(s)", filePath, loaded);
        }
    }

    private sealed record Entry(
        [property: JsonPropertyName("k")] string Key,
        [property: JsonPropertyName("e")] DateTimeOffset? ExpiresAtUtc);
}
