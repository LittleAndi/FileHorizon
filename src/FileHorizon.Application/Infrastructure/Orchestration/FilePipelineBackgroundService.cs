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
            var interval = _options.CurrentValue.IntervalMilliseconds;
            try
            {
                var cycleStart = DateTimeOffset.UtcNow;
                _logger.LogDebug("Pipeline cycle start at {StartUtc} (interval {Interval}ms)", cycleStart, interval);
                // 1. Poll source once per cycle
                var pollResult = await _poller.PollAsync(stoppingToken).ConfigureAwait(false);
                if (pollResult.IsFailure)
                {
                    _logger.LogWarning("Polling failed: {Error}", pollResult.Error);
                }

                // 2. Drain up to batch limit without blocking
                var batchLimit = _options.CurrentValue.BatchReadLimit;
                var drained = _queue.TryDrain(batchLimit);
                if (drained.Count > 0)
                {
                    _logger.LogDebug("Processing {Count} file events", drained.Count);
                }
                var processed = 0;
                foreach (var fe in drained)
                {
                    var result = await _processingService.HandleAsync(fe, stoppingToken).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("Processing failure for {FileId}: {Error}", fe.Id, result.Error);
                    }
                    processed++;
                }

                // 3. Sleep until next cycle accounting for work duration
                var elapsed = (int)(DateTimeOffset.UtcNow - cycleStart).TotalMilliseconds;
                if (elapsed > interval)
                {
                    _logger.LogWarning("Pipeline cycle overran interval: elapsed {Elapsed}ms > interval {Interval}ms", elapsed, interval);
                }
                else
                {
                    var remaining = interval - elapsed;
                    await Task.Delay(remaining, stoppingToken).ConfigureAwait(false);
                }
                _logger.LogDebug("Pipeline cycle end (elapsed {Elapsed}ms)", elapsed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in pipeline loop");
                // brief backoff to avoid tight crash loop
                await Task.Delay(Math.Min(2000, _options.CurrentValue.IntervalMilliseconds), stoppingToken).ConfigureAwait(false);
            }
        }
        _logger.LogInformation("File pipeline service stopping");
    }
}
