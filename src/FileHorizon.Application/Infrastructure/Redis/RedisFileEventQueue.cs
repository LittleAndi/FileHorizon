using System.Runtime.CompilerServices;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FileHorizon.Application.Infrastructure.Redis;

/// <summary>
/// Redis Streams implementation of <see cref="IFileEventQueue"/>.
/// Uses a single stream for all file events and a configured consumer group for coordinated delivery.
/// This implementation focuses on enqueue + basic group consumption; pending / claim logic can be added later.
/// </summary>
public sealed class RedisFileEventQueue : IFileEventQueue, IAsyncDisposable
{
    private readonly ILogger<RedisFileEventQueue> _logger;
    private readonly IFileEventValidator _validator;
    private readonly RedisOptions _options;
    private readonly ConnectionMultiplexer _connection;
    private readonly IDatabase _db;
    private readonly string _consumerName;

    public RedisFileEventQueue(
        ILogger<RedisFileEventQueue> logger,
        IFileEventValidator validator,
        IOptions<RedisOptions> options)
    {
        _logger = logger;
        _validator = validator;
        _options = options.Value;
        _options.Validate();

        var configuration = !string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? _options.ConnectionString
            : $"{_options.Host}:{_options.Port}";

        _connection = ConnectionMultiplexer.Connect(configuration);
        _db = _connection.GetDatabase();

        _consumerName = $"{_options.ConsumerNamePrefix}-{Environment.MachineName}-{Guid.NewGuid():N}";

        EnsureStreamAndGroupAsync().GetAwaiter().GetResult();
        _logger.LogInformation("RedisFileEventQueue initialized for stream {Stream} group {Group} consumer {Consumer}", _options.StreamName, _options.ConsumerGroup, _consumerName);
    }

    private async Task EnsureStreamAndGroupAsync()
    {
        if (!_options.CreateStreamIfMissing) return;
        var key = new RedisKey(_options.StreamName);
        try
        {
            // XGROUP CREATE <key> <groupname> $ MKSTREAM
            var server = GetServer();
            // We attempt a group create; if it exists we'll swallow BUSYGROUP.
            var db = _db;
            // Create stream implicitly by adding a dummy entry (deleted later) OR use XGROUP CREATE with MKSTREAM via Execute.
            var result = await db.ExecuteAsync("XGROUP", "CREATE", _options.StreamName, _options.ConsumerGroup, "$", "MKSTREAM").ConfigureAwait(false);
            _logger.LogDebug("Created consumer group {Group} on stream {Stream}: {Result}", _options.ConsumerGroup, _options.StreamName, result.ToString());
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Consumer group {Group} already exists on stream {Stream}", _options.ConsumerGroup, _options.StreamName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure stream/group {Stream}/{Group}. Continuing - operations may fail until resolved.", _options.StreamName, _options.ConsumerGroup);
        }
    }

    private IServer GetServer()
    {
        // Pick first endpoint; for clustered deployments a more elaborate strategy may be needed.
        var endpoint = _connection.GetEndPoints().First();
        return _connection.GetServer(endpoint);
    }

    public async Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct)
    {
        var validation = _validator.Validate(fileEvent);
        if (validation.IsFailure)
        {
            return Result.Failure(validation.Error);
        }
        if (ct.IsCancellationRequested)
        {
            return Result.Failure(Error.Unspecified("Queue.EnqueueCancelled", "Enqueue was cancelled"));
        }

        var values = new NameValueEntry[]
        {
            new("id", fileEvent.Id),
            new("sourcePath", fileEvent.Metadata.SourcePath),
            new("sizeBytes", fileEvent.Metadata.SizeBytes.ToString()),
            new("lastModifiedUtc", fileEvent.Metadata.LastModifiedUtc.ToUnixTimeMilliseconds()),
            new("hashAlgorithm", fileEvent.Metadata.HashAlgorithm),
            new("checksum", fileEvent.Metadata.Checksum ?? string.Empty),
            new("discoveredAtUtc", fileEvent.DiscoveredAtUtc.ToUnixTimeMilliseconds()),
            new("protocol", fileEvent.Protocol),
            new("destinationPath", fileEvent.DestinationPath)
        };

        try
        {
            var entryId = await _db.StreamAddAsync(_options.StreamName, values).ConfigureAwait(false);
            _logger.LogDebug("Enqueued file event {FileId} as stream entry {EntryId}", fileEvent.Id, entryId);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue file event {FileId}", fileEvent.Id);
            return Result.Failure(Error.Unspecified("Redis.EnqueueFailed", ex.Message));
        }
    }

    public async IAsyncEnumerable<FileEvent> DequeueAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var entries = await TryReadBatchAsync(ct).ConfigureAwait(false);
            if (entries is null)
            {
                // transient error already logged + backoff applied inside TryReadBatchAsync
                continue;
            }

            if (entries.Length == 0)
            {
                await Task.Delay(_options.ReadBlockMilliseconds, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var entry in entries)
            {
                if (ct.IsCancellationRequested) yield break;
                var fe = await ProcessEntryAsync(entry).ConfigureAwait(false);
                if (fe != null)
                {
                    yield return fe;
                }
            }
        }
    }

    private async Task<StreamEntry[]?> TryReadBatchAsync(CancellationToken ct)
    {
        try
        {
            return await _db.StreamReadGroupAsync(
                _options.StreamName,
                _options.ConsumerGroup,
                _consumerName,
                ">",
                count: _options.ReadBatchSize,
                flags: CommandFlags.None).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("NOGROUP"))
        {
            _logger.LogWarning("Consumer group missing; attempting to recreate");
            await EnsureStreamAndGroupAsync().ConfigureAwait(false);
            return Array.Empty<StreamEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading from stream {Stream}", _options.StreamName);
            await Task.Delay(500, ct).ConfigureAwait(false);
            return null; // signal transient failure
        }
    }

    private async Task<FileEvent?> ProcessEntryAsync(StreamEntry entry)
    {
        var fileEvent = MapEntryToFileEvent(entry);
        if (fileEvent is null)
        {
            _logger.LogWarning("Skipping malformed stream entry {EntryId}", entry.Id);
            if (!entry.Id.IsNullOrEmpty)
            {
                await AckAsync(entry.Id).ConfigureAwait(false);
            }
            return null;
        }

        if (!entry.Id.IsNullOrEmpty)
        {
            await AckAsync(entry.Id).ConfigureAwait(false);
        }
        return fileEvent;
    }

    private async Task AckAsync(RedisValue entryId)
    {
        try
        {
            if (entryId.IsNullOrEmpty) return;
            await _db.StreamAcknowledgeAsync(_options.StreamName, _options.ConsumerGroup, entryId!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acknowledge stream entry {EntryId}", entryId);
        }
    }

    private static FileEvent? MapEntryToFileEvent(StreamEntry entry)
    {
        try
        {
            var dict = entry.Values.ToDictionary(v => (string)v.Name!, v => v.Value.ToString());
            var id = dict.GetValueOrDefault("id") ?? string.Empty;
            var sourcePath = dict.GetValueOrDefault("sourcePath") ?? string.Empty;
            var sizeBytesStr = dict.GetValueOrDefault("sizeBytes") ?? "0";
            var lastModifiedMsStr = dict.GetValueOrDefault("lastModifiedUtc") ?? "0";
            var hashAlgorithm = dict.GetValueOrDefault("hashAlgorithm") ?? string.Empty;
            var checksum = dict.GetValueOrDefault("checksum");
            var discoveredAtMsStr = dict.GetValueOrDefault("discoveredAtUtc") ?? "0";
            var protocol = dict.GetValueOrDefault("protocol") ?? string.Empty;
            var destinationPath = dict.GetValueOrDefault("destinationPath") ?? string.Empty;

            if (!long.TryParse(sizeBytesStr, out var sizeBytes)) return null;
            if (!long.TryParse(lastModifiedMsStr, out var lastModifiedMs)) return null;
            if (!long.TryParse(discoveredAtMsStr, out var discoveredAtMs)) return null;

            var metadata = new FileMetadata(sourcePath, sizeBytes, DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMs), hashAlgorithm, checksum);
            return new FileEvent(id, metadata, DateTimeOffset.FromUnixTimeMilliseconds(discoveredAtMs), protocol, destinationPath);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyCollection<FileEvent> TryDrain(int maxCount)
    {
        if (maxCount <= 0) return Array.Empty<FileEvent>();
        try
        {
            var entries = _db.StreamReadGroup(
                _options.StreamName,
                _options.ConsumerGroup,
                _consumerName,
                ">",
                count: maxCount);
            if (entries.Length == 0) return Array.Empty<FileEvent>();

            var list = new List<FileEvent>(entries.Length);
            foreach (var entry in entries)
            {
                var fe = MapEntryToFileEvent(entry);

                // Always attempt to acknowledge (even malformed) to avoid stuck entries.
                if (!entry.Id.IsNullOrEmpty)
                {
                    _ = _db.StreamAcknowledgeAsync(_options.StreamName, _options.ConsumerGroup, entry.Id); // fire & forget
                }

                if (fe != null)
                {
                    list.Add(fe);
                }
                else
                {
                    _logger.LogDebug("Malformed stream entry {EntryId} ignored during TryDrain", entry.Id);
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryDrain failed for stream {Stream}", _options.StreamName);
            return Array.Empty<FileEvent>();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _connection.CloseAsync();
            _connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing Redis connection");
        }
    }
}
