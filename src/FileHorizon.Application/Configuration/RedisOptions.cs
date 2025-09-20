namespace FileHorizon.Application.Configuration;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>
    /// Enable Redis-backed queue. If false or not configured, system falls back to in-memory queue.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Full connection string (preferred for now). Future: support managed identity / auth token flows.
    /// If provided overrides Host/Port.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Host name of the Redis server (ignored if ConnectionString supplied).
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// TCP port for Redis (ignored if ConnectionString supplied).
    /// </summary>
    public int Port { get; set; } = 6379;

    /// <summary>
    /// Name of the Redis Stream used to coordinate file events.
    /// </summary>
    public string StreamName { get; set; } = "filehorizon:file-events";

    /// <summary>
    /// Consumer group name for coordinated processing.
    /// </summary>
    public string ConsumerGroup { get; set; } = "filehorizon-workers";

    /// <summary>
    /// Prefix for individual consumer names (actual name may append machine/instance id).
    /// </summary>
    public string ConsumerNamePrefix { get; set; } = "fh";

    /// <summary>
    /// Maximum number of entries to read per batch call.
    /// </summary>
    public int ReadBatchSize { get; set; } = 10;

    /// <summary>
    /// Create the stream and consumer group automatically if missing.
    /// </summary>
    public bool CreateStreamIfMissing { get; set; } = true;

    /// <summary>
    /// Optional idle timeout (ms) for Read operations before returning (allows cooperative cancellation).
    /// </summary>
    public int ReadBlockMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Validate option values; throws ArgumentException on invalid configuration.
    /// </summary>
    public void Validate()
    {
        if (ReadBatchSize <= 0) throw new ArgumentException("ReadBatchSize must be > 0", nameof(ReadBatchSize));
        if (ReadBlockMilliseconds <= 0) throw new ArgumentException("ReadBlockMilliseconds must be > 0", nameof(ReadBlockMilliseconds));
        if (string.IsNullOrWhiteSpace(StreamName)) throw new ArgumentException("StreamName is required", nameof(StreamName));
        if (string.IsNullOrWhiteSpace(ConsumerGroup)) throw new ArgumentException("ConsumerGroup is required", nameof(ConsumerGroup));
        if (string.IsNullOrWhiteSpace(ConsumerNamePrefix)) throw new ArgumentException("ConsumerNamePrefix is required", nameof(ConsumerNamePrefix));
    }
}
