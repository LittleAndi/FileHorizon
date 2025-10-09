using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Notifications;

/// <summary>
/// Real Service Bus notifier (Phase: connection string only). Handles idempotent suppression and basic retry.
/// Future phases will add AAD/SAS auth modes and richer error classification.
/// </summary>
public sealed class ServiceBusFileProcessedNotifier : IFileProcessedNotifier, IAsyncDisposable
{
    private readonly IOptionsMonitor<ServiceBusNotificationOptions> _options;
    private readonly ISecretResolver _secretResolver;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IFileProcessingTelemetry _telemetry;
    private readonly ILogger<ServiceBusFileProcessedNotifier> _logger;
    private ServiceBusClient? _client;
    private ServiceBusSender? _sender;
    private readonly object _initLock = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _circuitOpenedUtc;

    public ServiceBusFileProcessedNotifier(
        IOptionsMonitor<ServiceBusNotificationOptions> options,
        ISecretResolver secretResolver,
        IIdempotencyStore idempotencyStore,
        IFileProcessingTelemetry telemetry,
        ILogger<ServiceBusFileProcessedNotifier> logger)
    {
        _options = options;
        _secretResolver = secretResolver;
        _idempotencyStore = idempotencyStore;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<Result> PublishAsync(FileProcessedNotification notification, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            _telemetry.RecordNotificationSuppressed();
            return Result.Success();
        }

        // Circuit breaker short-circuit check
        if (opts.CircuitBreakerEnabled && IsCircuitOpen(opts))
        {
            _telemetry.RecordNotificationFailure("circuit.open");
            _logger.LogWarning("Circuit breaker OPEN - skipping publish. OpenedAt={OpenedAt:o}", _circuitOpenedUtc);
            return Result.Failure(Error.Unspecified("Notify.CircuitOpen", "Notification circuit breaker open"));
        }
        if (opts.AuthMode != ServiceBusAuthMode.ConnectionString)
        {
            _logger.LogWarning("ServiceBus notifier only supports ConnectionString mode in current phase. AuthMode={Mode}", opts.AuthMode);
            _telemetry.RecordNotificationFailure("auth.mode.unsupported");
            return Result.Failure(Error.Unspecified("Notify.AuthModeUnsupported", "Unsupported auth mode in current phase"));
        }
        if (string.IsNullOrWhiteSpace(opts.ConnectionSecretRef) || string.IsNullOrWhiteSpace(opts.EntityName))
        {
            _telemetry.RecordNotificationFailure("config.missing");
            return Result.Failure(Error.Validation.Invalid("ServiceBus configuration incomplete"));
        }

        // Idempotent suppression
        var suppressionKey = $"notify:{notification.IdempotencyKey}:{notification.Status}";
        var ttl = TimeSpan.FromMinutes(Math.Max(1, opts.IdempotencyTtlMinutes));
        var first = await _idempotencyStore.TryMarkProcessedAsync(suppressionKey, ttl, ct).ConfigureAwait(false);
        if (!first)
        {
            _telemetry.RecordNotificationSuppressed();
            _logger.LogDebug("Suppressed duplicate notification {Key}", suppressionKey);
            return Result.Success();
        }

        // Lazy init
        EnsureInitialized(opts, ct);
        if (_sender is null)
        {
            _telemetry.RecordNotificationFailure("sender.init");
            return Result.Failure(Error.Unspecified("Notify.SenderInitFailed", "Service Bus sender not initialized"));
        }

        var json = JsonSerializer.Serialize(notification);
        var message = new ServiceBusMessage(json)
        {
            ContentType = "application/json"
        };
        message.ApplicationProperties["file.protocol"] = notification.Protocol;
        message.ApplicationProperties["notify.status"] = notification.Status.ToString();
        message.ApplicationProperties["notify.schema.version"] = notification.SchemaVersion;
        message.ApplicationProperties["notify.id.key.prefix"] = notification.IdempotencyKey.Length <= 8 ? notification.IdempotencyKey : notification.IdempotencyKey[..8];

        var attempt = 0;
        var maxAttempts = Math.Max(1, opts.MaxRetryAttempts);
        var baseMs = Math.Max(10, opts.BaseRetryDelayMs);
        var maxMs = Math.Max(baseMs, opts.MaxRetryDelayMs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                attempt++;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(opts.PublishTimeoutSeconds));
                await _sender.SendMessageAsync(message, timeoutCts.Token).ConfigureAwait(false);
                _telemetry.RecordNotificationSuccess(0); // duration captured at orchestrator level; here we just count
                // Success resets failure counters & circuit state
                _consecutiveFailures = 0;
                _circuitOpenedUtc = null;
                return Result.Success();
            }
            catch (Exception ex)
            {
                var transient = IsTransient(ex);
                var shouldRetry = transient && attempt < maxAttempts;

                if (shouldRetry)
                {
                    _logger.LogWarning(ex, "Transient publish failure attempt {Attempt}/{Max}", attempt, maxAttempts);
                    var delay = ComputeDelay(baseMs, attempt, maxMs, opts.EnableJitter);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                _consecutiveFailures++;
                _logger.LogError(ex, "ServiceBus publish failed. Attempt={Attempt} Transient={Transient} ConsecutiveFailures={Failures}", attempt, transient, _consecutiveFailures);
                var reason = transient ? "publish.error.transient.maxretries" : "publish.error.terminal";
                _telemetry.RecordNotificationFailure(reason);

                if (opts.CircuitBreakerEnabled && _consecutiveFailures >= opts.CircuitBreakerFailureThreshold)
                {
                    _circuitOpenedUtc = DateTimeOffset.UtcNow;
                    _logger.LogWarning("Circuit breaker OPENED after {Failures} consecutive failures", _consecutiveFailures);
                }

                return Result.Failure(Error.Unspecified("Notify.PublishFailed", ex.Message));
            }
        }
    }

    private static TimeSpan ComputeDelay(int baseMs, int attempt, int maxMs, bool jitter)
    {
        var exp = baseMs * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exp, maxMs);
        if (!jitter) return TimeSpan.FromMilliseconds(capped);
        var rand = Random.Shared.NextDouble() * 0.5 + 0.75; // 0.75 - 1.25x
        return TimeSpan.FromMilliseconds(capped * rand);
    }

    private void EnsureInitialized(ServiceBusNotificationOptions opts, CancellationToken ct)
    {
        if (_client is not null && _sender is not null) return;
        lock (_initLock)
        {
            if (_client is not null && _sender is not null) return;
            var conn = _secretResolver.ResolveSecretAsync(opts.ConnectionSecretRef!, ct).GetAwaiter().GetResult();
            _client = new ServiceBusClient(conn);
            _sender = _client.CreateSender(opts.EntityName!);
        }
    }

    private bool IsCircuitOpen(ServiceBusNotificationOptions opts)
    {
        if (_circuitOpenedUtc is null) return false;
        var resetAfter = _circuitOpenedUtc.Value.AddSeconds(opts.CircuitBreakerResetSeconds);
        if (DateTimeOffset.UtcNow >= resetAfter)
        {
            // Half-open: allow a trial attempt; reset counters
            _consecutiveFailures = 0;
            _circuitOpenedUtc = null;
            return false;
        }
        return true;
    }

    private static bool IsTransient(Exception ex)
    {
        return ex switch
        {
            ServiceBusException sb when sb.IsTransient => true,
            TimeoutException => true,
            TaskCanceledException => true,
            OperationCanceledException => true,
            _ => false
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null)
        {
            await _sender.DisposeAsync().ConfigureAwait(false);
        }
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
