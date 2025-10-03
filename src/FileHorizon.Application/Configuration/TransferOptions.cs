namespace FileHorizon.Application.Configuration;

public sealed class TransferOptions
{
    public const string SectionName = "Transfer";
    public int MaxConcurrentPerDestination { get; set; } = 2;
    public RetryOptions Retry { get; set; } = new();
    public int ChunkSizeBytes { get; set; } = 1024 * 1024; // 1MB default
    public ChecksumOptions Checksum { get; set; } = new();
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 3;
    public int BackoffBaseMs { get; set; } = 200;
    public int BackoffMaxMs { get; set; } = 10_000;
}

public sealed class ChecksumOptions
{
    public string Algorithm { get; set; } = "none"; // none|md5|sha256
}
