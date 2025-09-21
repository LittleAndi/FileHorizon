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

    private const int MinDelayMs = 25;
    private const int MaxDelayMs = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File processing service starting");
        var delay = new AdaptiveDelay(MinDelayMs, MaxDelayMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = DrainQueue();
                if (batch.Count == 0)
                {
                    await delay.DelayAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                delay.Reset();
                await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await HandleLoopExceptionAsync(ex, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("File processing service stopping");
    }

    private IReadOnlyCollection<Models.FileEvent> DrainQueue()
    {
        var options = _pollingOptions.CurrentValue;
        return _queue.TryDrain(options.BatchReadLimit);
    }

    private async Task ProcessBatchAsync(IReadOnlyCollection<Models.FileEvent> events, CancellationToken ct)
    {
        if (events.Count > 0)
        {
            _logger.LogDebug("Processing {Count} file events", events.Count);
        }

        foreach (var fe in events)
        {
            if (ct.IsCancellationRequested) break; // graceful early exit

            var result = await _processingService.HandleAsync(fe, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Processing failure for {FileId}: {Error}", fe.Id, result.Error);
            }
        }
    }

    private async Task HandleLoopExceptionAsync(Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Unhandled exception in processing loop");
        await Task.Delay(250, ct).ConfigureAwait(false);
    }

    private sealed class AdaptiveDelay
    {
        private readonly int _min;
        private readonly int _max;
        private int _current;

        public AdaptiveDelay(int min, int max)
        {
            _min = min;
            _max = max;
            _current = min;
        }

        public async Task DelayAsync(CancellationToken ct)
        {
            _current = Math.Min(_current * 2, _max);
            await Task.Delay(_current, ct).ConfigureAwait(false);
        }

        public void Reset() => _current = _min;
    }
}
