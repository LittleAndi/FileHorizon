namespace FileHorizon.Application.Configuration;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";
    public bool Enabled { get; set; } = false;
    public int TtlSeconds { get; set; } = 86400; // 24h
}
