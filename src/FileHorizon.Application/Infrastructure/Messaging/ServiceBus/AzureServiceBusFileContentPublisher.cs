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

        var attempt = 0;
        var maxRetries = Math.Max(0, _options.PublishRetryCount);
        var baseDelay = TimeSpan.FromMilliseconds(Math.Max(0, _options.PublishRetryBaseDelayMs));
        var maxDelay = TimeSpan.FromMilliseconds(Math.Max(_options.PublishRetryBaseDelayMs, _options.PublishRetryMaxDelayMs));
        ServiceBusException? lastTransient = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sender = _client!.CreateSender(request.DestinationName);
                var message = CreateMessage(request.Content, request);
                await sender.SendMessageAsync(message, ct).ConfigureAwait(false);
                if (attempt > 0)
                {
                    _logger.LogInformation("Published file {FileName} to {Destination} after {Attempts} attempt(s)", request.FileName, request.DestinationName, attempt + 1);
                }
                else
                {
                    _logger.LogDebug("Published file {FileName} to {Destination}", request.FileName, request.DestinationName);
                }
                return Result.Success();
            }
            catch (ServiceBusException sbEx) when (sbEx.IsTransient)
            {
                lastTransient = sbEx;
                if (attempt >= maxRetries)
                {
                    _logger.LogWarning(sbEx, "Transient failure publishing file {FileName} to {Destination} after {Attempts} attempts (giving up)", request.FileName, request.DestinationName, attempt + 1);
                    return Result.Failure(Error.Messaging.PublishTransient(sbEx.Message));
                }
                var delay = CalculateBackoff(attempt, baseDelay, maxDelay);
                attempt++;
                _logger.LogWarning(sbEx, "Transient failure publishing file {FileName} to {Destination}. Retrying attempt {Attempt} of {Max} in {Delay} ms", request.FileName, request.DestinationName, attempt, maxRetries + 1, delay.TotalMilliseconds);
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Result.Failure(Error.Messaging.PublishTransient("Publish cancelled during backoff"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed publishing file {FileName} to {Destination}", request.FileName, request.DestinationName);
                return Result.Failure(Error.Messaging.PublishError(ex.Message));
            }
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

    private static TimeSpan CalculateBackoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        // Exponential backoff with jitter: delay = min(maxDelay, baseDelay * 2^attempt) * (0.5 + rand/2)
        var exponent = Math.Pow(2, attempt);
        var raw = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * exponent);
        if (raw > maxDelay) raw = maxDelay;
        var jitterFactor = 0.5 + Random.Shared.NextDouble() / 2.0; // 0.5 - 1.0 range
        var jittered = TimeSpan.FromMilliseconds(raw.TotalMilliseconds * jitterFactor);
        return jittered;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
