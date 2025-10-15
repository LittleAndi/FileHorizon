using Azure.Messaging.ServiceBus;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common; // Assuming Result is here
using FileHorizon.Application.Common.Telemetry;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace FileHorizon.Application.Infrastructure.Messaging.ServiceBus;

/// <summary>
/// Azure Service Bus implementation of <see cref="IFileContentPublisher"/>.
/// Supports publishing whole file content as a single message. (Per-line mode removed)
/// </summary>
public sealed class AzureServiceBusFileContentPublisher : IFileContentPublisher, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ServiceBusClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _destinationConcurrency = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _enabled;
    private readonly ILogger<AzureServiceBusFileContentPublisher> _logger;
    private readonly IOptionsMonitor<DestinationsOptions>? _destinations; // provides both destination list and technical options
    // Metrics instruments now attached to shared application meter (TelemetryInstrumentation.Meter)
    private static readonly Counter<long> _publishAttempts = TelemetryInstrumentation.Meter.CreateCounter<long>("servicebus.publish.attempts", description: "Number of publish attempts (including retries)");
    private static readonly Counter<long> _publishSuccesses = TelemetryInstrumentation.Meter.CreateCounter<long>("servicebus.publish.successes", description: "Number of successful publishes");
    private static readonly Counter<long> _publishFailures = TelemetryInstrumentation.Meter.CreateCounter<long>("servicebus.publish.failures", description: "Number of failed publishes after retries exhausted");
    private static readonly Counter<long> _publishRetries = TelemetryInstrumentation.Meter.CreateCounter<long>("servicebus.publish.retries", description: "Number of retry attempts due to transient errors");
    private static readonly Histogram<double> _retryDelayMs = TelemetryInstrumentation.Meter.CreateHistogram<double>("servicebus.publish.retry_delay.ms", unit: "ms", description: "Backoff delay milliseconds for each retry attempt");

    public AzureServiceBusFileContentPublisher(
        ILogger<AzureServiceBusFileContentPublisher> logger,
        IOptionsMonitor<DestinationsOptions>? destinations = null)
    {
        _logger = logger;
        _destinations = destinations; // may be null in isolated unit tests

        // Enabled if any ServiceBus destination has a connection string OR any has managed identity namespace configured.
        var current = _destinations?.CurrentValue.ServiceBus;
        var anyCs = current?.Any(d => !string.IsNullOrWhiteSpace(d.ServiceBusTechnical.ConnectionString)) == true;
        var anyNamespace = current?.Any(d => !string.IsNullOrWhiteSpace(d.ServiceBusTechnical.FullyQualifiedNamespace)) == true;
        _enabled = anyCs || anyNamespace;
        if (_enabled)
        {
            if (anyCs && anyNamespace)
            {
                _logger.LogInformation("Service Bus publisher enabled (mixed mode: destination connection strings + managed identity namespaces)");
            }
            else if (anyCs)
            {
                _logger.LogInformation("Service Bus publisher enabled (per-destination connection strings)");
            }
            else
            {
                _logger.LogInformation("Service Bus publisher enabled (managed identity namespaces)");
            }
        }
        else
        {
            _logger.LogWarning("Service Bus publisher disabled: no destination connection strings or managed identity namespaces configured");
        }
    }

    public async Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Skipping publish for {FileName} because Service Bus publisher is disabled", request.FileName);
            return Result.Success();
        }
        var validation = ValidateRequest(request);
        if (validation.IsFailure) return validation;

        var (destination, entityName, tech) = ResolveContext(request.DestinationName);
        if (entityName is null)
        {
            return Result.Failure(Error.Messaging.PublishError("Destination entity not resolved"));
        }
        var client = GetOrCreateClient(destination, tech);
        if (client is null)
        {
            return Result.Failure(Error.Messaging.PublishError("Service Bus client unavailable"));
        }
        var semaphore = GetSemaphore(destination, tech.MaxConcurrentPublishes);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendWithRetryAsync(client, entityName, tech, request, ct).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static Result ValidateRequest(FilePublishRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationName))
        {
            return Result.Failure(Error.Messaging.DestinationEmpty);
        }
        if (request.Content.IsEmpty)
        {
            return Result.Failure(Error.Messaging.ContentEmpty);
        }
        return Result.Success();
    }

    private (string destinationName, string? entityName, ServiceBusTechnicalOptions tech) ResolveContext(string logicalDestination)
    {
        var dest = ResolveDestination(logicalDestination);
        if (dest is null)
        {
            // Fallback: create ephemeral default technical options
            return (logicalDestination, logicalDestination, new ServiceBusTechnicalOptions());
        }
        var entityName = string.IsNullOrWhiteSpace(dest.EntityName) ? logicalDestination : dest.EntityName;
        var tech = dest.ServiceBusTechnical ?? new ServiceBusTechnicalOptions();
        return (dest.Name, entityName, tech);
    }

    private async Task<Result> SendWithRetryAsync(ServiceBusClient client, string entityName, ServiceBusTechnicalOptions tech, FilePublishRequest request, CancellationToken ct)
    {
        var attempt = 0;
        var maxRetries = Math.Max(0, tech.PublishRetryCount);
        var baseDelay = TimeSpan.FromMilliseconds(Math.Max(0, tech.PublishRetryBaseDelayMs));
        var maxDelay = TimeSpan.FromMilliseconds(Math.Max(tech.PublishRetryBaseDelayMs, tech.PublishRetryMaxDelayMs));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _publishAttempts.Add(1, new KeyValuePair<string, object?>("destination", request.DestinationName));
                var sender = client.CreateSender(entityName);
                var message = CreateMessage(request.Content, request);
                await sender.SendMessageAsync(message, ct).ConfigureAwait(false);
                _publishSuccesses.Add(1, new KeyValuePair<string, object?>("destination", request.DestinationName));
                LogPublishSuccess(request.FileName, request.DestinationName, entityName, attempt);
                return Result.Success();
            }
            catch (ServiceBusException sbEx) when (sbEx.IsTransient)
            {
                if (attempt >= maxRetries)
                {
                    _logger.LogWarning(sbEx, "Transient failure publishing file {FileName} to {Destination} after {Attempts} attempts (giving up)", request.FileName, request.DestinationName, attempt + 1);
                    _publishFailures.Add(1, new KeyValuePair<string, object?>("destination", request.DestinationName));
                    return Result.Failure(Error.Messaging.PublishTransient(sbEx.Message));
                }
                var delay = CalculateBackoff(attempt, baseDelay, maxDelay);
                attempt++;
                _publishRetries.Add(1, new KeyValuePair<string, object?>("destination", request.DestinationName));
                _retryDelayMs.Record(delay.TotalMilliseconds, new KeyValuePair<string, object?>("destination", request.DestinationName), new KeyValuePair<string, object?>("attempt", attempt));
                _logger.LogWarning(sbEx, "Transient failure publishing file {FileName} to {Destination}. Retrying attempt {Attempt} of {Max} in {Delay} ms", request.FileName, request.DestinationName, attempt, maxRetries + 1, delay.TotalMilliseconds);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return Result.Failure(Error.Messaging.PublishTransient("Publish cancelled during backoff")); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed publishing file {FileName} to {Destination}", request.FileName, request.DestinationName);
                _publishFailures.Add(1, new KeyValuePair<string, object?>("destination", request.DestinationName));
                return Result.Failure(Error.Messaging.PublishError(ex.Message));
            }
        }
    }

    private void LogPublishSuccess(string fileName, string destination, string entityName, int attempt)
    {
        if (attempt > 0)
        {
            _logger.LogInformation("Published file {FileName} to {Destination} (entity {Entity}) after {Attempts} attempt(s)", fileName, destination, entityName, attempt + 1);
        }
        else
        {
            _logger.LogDebug("Published file {FileName} to {Destination} (entity {Entity})", fileName, destination, entityName);
        }
    }

    private ServiceBusDestinationOptions? ResolveDestination(string logicalName)
    {
        try
        {
            var dests = _destinations?.CurrentValue.ServiceBus;
            if (dests is null) return null;
            var match = dests.FirstOrDefault(d => string.Equals(d.Name, logicalName, StringComparison.OrdinalIgnoreCase));
            return match;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed resolving entity name for logical destination {Destination}", logicalName);
            return null; // fallback to logical name
        }
    }

    private string? ResolveEntityName(string logicalName)
    {
        var dest = ResolveDestination(logicalName);
        if (dest is null) return null;
        if (string.IsNullOrWhiteSpace(dest.EntityName)) return null;
        return dest.EntityName;
    }

    private ServiceBusClient? GetOrCreateClient(string destinationName, ServiceBusTechnicalOptions tech)
    {
        // Prefer connection string
        if (!string.IsNullOrWhiteSpace(tech.ConnectionString))
        {
            return _clients.GetOrAdd("cs:" + tech.ConnectionString, cs => new ServiceBusClient(tech.ConnectionString));
        }
        // Managed identity namespace path
        if (!string.IsNullOrWhiteSpace(tech.FullyQualifiedNamespace))
        {
            return _clients.GetOrAdd("mi:" + tech.FullyQualifiedNamespace, _ =>
            {
                Azure.Core.TokenCredential credential = string.IsNullOrWhiteSpace(tech.ManagedIdentityClientId)
                    ? new Azure.Identity.DefaultAzureCredential()
                    : new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions { ManagedIdentityClientId = tech.ManagedIdentityClientId });
                return new ServiceBusClient(tech.FullyQualifiedNamespace!, credential);
            });
        }
        return null;
    }

    private SemaphoreSlim GetSemaphore(string destinationName, int maxConcurrent)
    {
        var effective = maxConcurrent <= 0 ? 1 : maxConcurrent;
        return _destinationConcurrency.GetOrAdd(destinationName, _ => new SemaphoreSlim(effective, effective));
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
        foreach (var kv in _clients)
        {
            await kv.Value.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var sem in _destinationConcurrency.Values)
        {
            sem.Dispose();
        }
    }
}
