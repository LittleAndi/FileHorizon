namespace FileHorizon.Application.Models;

/// <summary>
/// Represents a request to publish file content to a messaging destination.
/// </summary>
public sealed record FilePublishRequest(
    string SourcePath,
    string FileName,
    ReadOnlyMemory<byte> Content,
    string? ContentType,
    string DestinationName,
    bool IsTopic,
    IReadOnlyDictionary<string, string>? ApplicationProperties = null
);
