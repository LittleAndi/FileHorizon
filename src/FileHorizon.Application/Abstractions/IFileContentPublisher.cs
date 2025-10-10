namespace FileHorizon.Application.Abstractions;

using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

/// <summary>
/// Publishes file content to a messaging destination (e.g., Azure Service Bus queue or topic).
/// Implementations must be idempotent when provided the same request (caller may handle idempotency via store).
/// </summary>
public interface IFileContentPublisher
{
    /// <summary>
    /// Publish the given file content request to the configured destination.
    /// </summary>
    /// <param name="request">The publication request containing destination and content details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating success or failure.</returns>
    Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct);
}
