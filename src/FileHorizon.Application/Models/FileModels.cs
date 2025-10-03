namespace FileHorizon.Application.Models;

public sealed record FileMetadata(
    string SourcePath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string HashAlgorithm,
    string? Checksum)
{
    public bool HasChecksum => !string.IsNullOrWhiteSpace(Checksum);
}

public sealed record FileEvent(
    string Id,
    FileMetadata Metadata,
    DateTimeOffset DiscoveredAtUtc,
    string Protocol,
    string DestinationPath,
    bool DeleteAfterTransfer = false);