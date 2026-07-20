namespace FileHorizon.Application.Models;

/// <summary>Fully resolved upload instruction handed to <see cref="Abstractions.IBlobStorageClient"/>.</summary>
public sealed record BlobUploadRequest(
    string DestinationName,
    string ContainerName,
    string BlobPath,
    string? ContentType,
    bool Overwrite,
    string? AccessTier);

/// <summary>Reference to the artifact produced by a successful blob upload.</summary>
public sealed record BlobUploadResult(
    string AccountName,
    string ContainerName,
    string BlobPath,
    Uri BlobUri,
    long BytesWritten);
