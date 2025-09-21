using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Orchestration;

/// <summary>
/// Background service responsible for draining queued file events and processing them.
/// </summary>
public sealed class FileProcessingBackgroundService(
    IFileEventQueue queue,
    Core.IFileProcessingService processingService,
    IOptionsMonitor<PollingOptions> pollingOptions,
    ILogger<FileProcessingBackgroundService> logger) : BackgroundService
{
    private readonly IFileEventQueue _queue = queue;
    private readonly Core.IFileProcessingService _processingService = processingService;
    private readonly IOptionsMonitor<PollingOptions> _pollingOptions = pollingOptions; // re-use BatchReadLimit & interval for now
    private readonly ILogger<FileProcessingBackgroundService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File processing service starting");

        // Adaptive delay parameters
        const int minDelayMs = 25;
        const int maxDelayMs = 500;
        var currentDelay = minDelayMs;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _pollingOptions.CurrentValue; // using BatchReadLimit
                var events = _queue.TryDrain(options.BatchReadLimit);
                if (events.Count == 0)
                {
                    // Exponential-ish backoff when idle
                    currentDelay = Math.Min(currentDelay * 2, maxDelayMs);
                    await Task.Delay(currentDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Reset delay once we have work
                currentDelay = minDelayMs;

                if (events.Count > 0)
                {
                    _logger.LogDebug("Processing {Count} file events", events.Count);
                }

                foreach (var fe in events)
                {
                    var result = await _processingService.HandleAsync(fe, stoppingToken).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("Processing failure for {FileId}: {Error}", fe.Id, result.Error);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in processing loop");
                await Task.Delay(250, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("File processing service stopping");
    }
}
