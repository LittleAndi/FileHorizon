using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Messaging.ServiceBus;

/// <summary>
/// No-op implementation of <see cref="IFileContentPublisher"/> used when Service Bus is not configured.
/// Treats all publish requests as successful and logs at debug level for traceability.
/// </summary>
public sealed class DisabledFileContentPublisher : IFileContentPublisher
{
    private readonly ILogger<DisabledFileContentPublisher> _logger;
    public DisabledFileContentPublisher(ILogger<DisabledFileContentPublisher> logger) => _logger = logger;

    public Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct)
    {
        _logger.LogDebug("Service Bus publisher disabled; skipping publish for {FileName}", request.FileName);
        return Task.FromResult(Result.Success());
    }
}
