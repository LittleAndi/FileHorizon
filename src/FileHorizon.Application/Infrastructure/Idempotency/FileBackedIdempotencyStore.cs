using System.Collections.Concurrent;
using System.Text.Json;
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

    /// <summary>
    /// Absolute path of the backing file. Exposed so a caller that reads idempotency files itself - the
    /// import command - can tell whether it has been pointed at this store's own file.
    /// </summary>
    public string FilePath { get; }

    public FileBackedIdempotencyStore(string filePath, ILogger<FileBackedIdempotencyStore> logger)
    {
        _logger = logger;
        FilePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LoadExistingEntries(FilePath);

        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
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
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new IdempotencyFileEntry(key, expiry))).ConfigureAwait(false);
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

        var contents = IdempotencyFileReader.Read(filePath);
        foreach (var entry in contents.Entries)
        {
            _entries[entry.Key] = entry.ExpiresAtUtc;
        }

        var loaded = contents.Entries.Count;
        var skipped = contents.SkippedLines;
        if (skipped > 0)
        {
            _logger.LogWarning("Idempotency file {Path}: skipped {Skipped} unreadable line(s) while loading {Loaded} marker(s)", filePath, skipped, loaded);
        }
        else
        {
            _logger.LogInformation("Idempotency file {Path}: loaded {Loaded} marker(s)", filePath, loaded);
        }
    }
}
