namespace FileHorizon.Application.Models.Notifications;

/// <summary>
/// Schema v1: immutable record describing the outcome of processing a file event.
/// </summary>
public sealed record FileProcessedNotification(
    int SchemaVersion,
    string Protocol,
    string FullPath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    ProcessingStatus Status,
    TimeSpan ProcessingDuration,
    string IdempotencyKey,
    string CorrelationId,
    DateTimeOffset CompletedUtc,
    IReadOnlyList<DestinationResult> Destinations
)
{
    public static FileProcessedNotification Create(
        string protocol,
        string fullPath,
        long sizeBytes,
        DateTimeOffset lastModifiedUtc,
        ProcessingStatus status,
        TimeSpan processingDuration,
        string idempotencyKey,
        string correlationId,
        DateTimeOffset completedUtc,
        IReadOnlyList<DestinationResult> destinations) => new(
            SchemaVersion: 1,
            Protocol: protocol,
            FullPath: fullPath,
            SizeBytes: sizeBytes,
            LastModifiedUtc: lastModifiedUtc,
            Status: status,
            ProcessingDuration: processingDuration,
            IdempotencyKey: idempotencyKey,
            CorrelationId: correlationId,
            CompletedUtc: completedUtc,
            Destinations: destinations);
}

public enum ProcessingStatus { Success, Failure }

public sealed record DestinationResult(
    string DestinationType,
    string DestinationIdentifier,
    bool Success,
    long BytesWritten,
    TimeSpan? Latency
);