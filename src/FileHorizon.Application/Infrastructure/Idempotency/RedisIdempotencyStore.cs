using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FileHorizon.Application.Infrastructure.Idempotency;

public sealed class RedisIdempotencyStore : IIdempotencyStore, IDisposable
{
    private readonly ILogger<RedisIdempotencyStore> _logger;
    private readonly IOptionsMonitor<RedisOptions> _options;
    private readonly ConnectionMultiplexer _connection;
    private readonly IDatabase _db;

    public RedisIdempotencyStore(ILogger<RedisIdempotencyStore> logger, IOptionsMonitor<RedisOptions> options)
    {
        _logger = logger;
        _options = options;
        var cfg = options.CurrentValue;
        var configuration = !string.IsNullOrWhiteSpace(cfg.ConnectionString)
            ? cfg.ConnectionString
            : $"{cfg.Host}:{cfg.Port}";
        _connection = ConnectionMultiplexer.Connect(configuration);
        _db = _connection.GetDatabase();
    }

    public async Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        var expiry = ttl ?? TimeSpan.FromHours(24);
        try
        {
            // SET key value NX EX seconds
            var ok = await _db.StringSetAsync(key, "1", expiry, When.NotExists).ConfigureAwait(false);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis idempotency SET failed for {Key}; treating as not marked to avoid false positives", key);
            return false;
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
