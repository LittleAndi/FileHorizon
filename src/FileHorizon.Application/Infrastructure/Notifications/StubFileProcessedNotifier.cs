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
    ILogger<StubFileProcessedNotifier> logger) : IFileProcessedNotifier
{
    private readonly IOptionsMonitor<ServiceBusNotificationOptions> _options = options;
    private readonly ILogger<StubFileProcessedNotifier> _logger = logger;

    public Task<Result> PublishAsync(FileProcessedNotification notification, CancellationToken ct)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult(Result.Success()); // disabled => noop
        }
        _logger.LogInformation("[NotifyStub] Would publish file notification {Path} status={Status} idempotency={Key}", notification.FullPath, notification.Status, notification.IdempotencyKey);
        return Task.FromResult(Result.Success());
    }
}