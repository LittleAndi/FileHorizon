using Azure.Messaging.ServiceBus;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common; // Assuming Result is here
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace FileHorizon.Application.Infrastructure.Messaging.ServiceBus;

/// <summary>
/// Azure Service Bus implementation of <see cref="IFileContentPublisher"/>.
/// Supports publishing whole file content as a single message. (Per-line mode removed)
/// </summary>
public sealed class AzureServiceBusFileContentPublisher : IFileContentPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient? _client;
    private readonly bool _enabled;
    private readonly ILogger<AzureServiceBusFileContentPublisher> _logger;
    private readonly ServiceBusPublisherOptions _options;

    public AzureServiceBusFileContentPublisher(
        IOptions<ServiceBusPublisherOptions> options,
        ILogger<AzureServiceBusFileContentPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            _enabled = false;
            _logger.LogWarning("Service Bus publisher disabled (no connection string configured)");
            return;
        }
        _client = new ServiceBusClient(_options.ConnectionString);
        _enabled = true;
    }

    public async Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Skipping publish for {FileName} because Service Bus publisher is disabled", request.FileName);
            return Result.Success(); // treat as no-op success in disabled mode
        }
        if (string.IsNullOrWhiteSpace(request.DestinationName))
        {
            return Result.Failure(Error.Messaging.DestinationEmpty);
        }
        if (request.Content.IsEmpty)
        {
            return Result.Failure(Error.Messaging.ContentEmpty);
        }

        try
        {
            var sender = _client!.CreateSender(request.DestinationName);
            var message = CreateMessage(request.Content, request);
            await sender.SendMessageAsync(message, ct).ConfigureAwait(false);
            _logger.LogDebug("Published file {FileName} to {Destination}", request.FileName, request.DestinationName);
            return Result.Success();
        }
        catch (ServiceBusException sbEx) when (sbEx.IsTransient)
        {
            _logger.LogWarning(sbEx, "Transient failure publishing file {FileName} to {Destination}.", request.FileName, request.DestinationName);
            return Result.Failure(Error.Messaging.PublishTransient(sbEx.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed publishing file {FileName} to {Destination}", request.FileName, request.DestinationName);
            return Result.Failure(Error.Messaging.PublishError(ex.Message));
        }
    }

    private static ServiceBusMessage CreateMessage(ReadOnlyMemory<byte> content, FilePublishRequest request)
    {
        var message = new ServiceBusMessage(content);
        if (!string.IsNullOrWhiteSpace(request.ContentType))
        {
            message.ContentType = request.ContentType;
        }
        if (request.ApplicationProperties is not null)
        {
            foreach (var kvp in request.ApplicationProperties)
            {
                message.ApplicationProperties[kvp.Key] = kvp.Value;
            }
        }
        message.Subject = request.FileName;
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
