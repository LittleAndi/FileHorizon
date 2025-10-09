using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Notifications;

/// <summary>
/// Phase 1 stub notifier: logs intent when enabled. No external dependencies.
/// </summary>
public sealed class StubFileProcessedNotifier(
    IOptionsMonitor<ServiceBusNotificationOptions> options,
    Abstractions.IIdempotencyStore idempotencyStore,
    Abstractions.IFileProcessingTelemetry telemetry,
    ILogger<StubFileProcessedNotifier> logger) : IFileProcessedNotifier
{
    private readonly IOptionsMonitor<ServiceBusNotificationOptions> _options = options;
    private readonly ILogger<StubFileProcessedNotifier> _logger = logger;
    private readonly Abstractions.IIdempotencyStore _idempotencyStore = idempotencyStore;
    private readonly Abstractions.IFileProcessingTelemetry _telemetry = telemetry;

    public async Task<Result> PublishAsync(FileProcessedNotification notification, CancellationToken ct)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _telemetry.RecordNotificationSuppressed();
            return Result.Success(); // disabled => noop
        }
        var key = $"notify:{notification.IdempotencyKey}:{notification.Status}";
        var ttl = TimeSpan.FromMinutes(10); // provisional TTL; future optionization
        var first = await _idempotencyStore.TryMarkProcessedAsync(key, ttl, ct).ConfigureAwait(false);
        if (!first)
        {
            _telemetry.RecordNotificationSuppressed();
            _logger.LogDebug("[NotifyStub] Suppressed duplicate notification {Key}", key);
            return Result.Success();
        }
        _logger.LogInformation("[NotifyStub] Would publish file notification {Path} status={Status} idempotency={Key}", notification.FullPath, notification.Status, notification.IdempotencyKey);
        return Result.Success();
    }
}