using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Orchestration;

/// <summary>
/// Background service that coordinates polling for new file events and processing queued events.
/// Simple sequential implementation (poll, then drain limited batch).
/// </summary>
public sealed class FilePipelineBackgroundService : BackgroundService
{
    private readonly IFilePoller _poller;
    private readonly IFileEventQueue _queue;
    private readonly Core.IFileProcessingService _processingService;
    private readonly ILogger<FilePipelineBackgroundService> _logger;
    private readonly IOptionsMonitor<PollingOptions> _options;

    public FilePipelineBackgroundService(
        IFilePoller poller,
        IFileEventQueue queue,
        Core.IFileProcessingService processingService,
        ILogger<FilePipelineBackgroundService> logger,
        IOptionsMonitor<PollingOptions> options)
    {
        _poller = poller;
        _queue = queue;
        _processingService = processingService;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File pipeline service starting");
        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStart = DateTimeOffset.UtcNow;
            var optionsSnapshot = _options.CurrentValue;

            try
            {
                await RunCycleAsync(optionsSnapshot, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in pipeline loop");
                // brief backoff
                var backoff = Math.Min(2000, optionsSnapshot.IntervalMilliseconds);
                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
            }

            var elapsedMs = (int)(DateTimeOffset.UtcNow - cycleStart).TotalMilliseconds;
            var remaining = optionsSnapshot.IntervalMilliseconds - elapsedMs;
            if (remaining > 0)
            {
                await Task.Delay(remaining, stoppingToken).ConfigureAwait(false);
            }
            else if (elapsedMs > optionsSnapshot.IntervalMilliseconds)
            {
                _logger.LogWarning("Pipeline cycle overran interval: elapsed {Elapsed}ms > interval {Interval}ms",
                    elapsedMs, optionsSnapshot.IntervalMilliseconds);
            }
        }
        _logger.LogInformation("File pipeline service stopping");
    }

    private async Task RunCycleAsync(PollingOptions options, CancellationToken ct)
    {
        _logger.LogDebug("Pipeline cycle start (interval {Interval}ms)", options.IntervalMilliseconds);

        var pollResult = await _poller.PollAsync(ct).ConfigureAwait(false);
        if (pollResult.IsFailure)
        {
            _logger.LogWarning("Polling failed: {Error}", pollResult.Error);
        }

        var events = _queue.TryDrain(options.BatchReadLimit);
        if (events.Count > 0)
        {
            _logger.LogDebug("Processing {Count} file events", events.Count);
        }

        foreach (var fe in events)
        {
            var result = await _processingService.HandleAsync(fe, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Processing failure for {FileId}: {Error}", fe.Id, result.Error);
            }
        }

        _logger.LogDebug("Pipeline cycle end");
    }
}
