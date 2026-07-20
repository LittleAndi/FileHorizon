namespace FileHorizon.Application.Configuration;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";
    public bool Enabled { get; set; } = false;

    /// <summary>Retention for processed-file markers in seconds. 0 (default) keeps markers forever.</summary>
    public int TtlSeconds { get; set; } = 0;

    /// <summary>
    /// Directory for the file-backed durable store, used when Redis is disabled or unavailable.
    /// When unset, marks are kept in memory only and do not survive restarts.
    /// </summary>
    public string? DataDirectory { get; set; }
}
