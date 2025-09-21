using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Orchestration;

/// <summary>
/// Background service that coordinates polling for new file events and processing queued events.
/// Simple sequential implementation (poll, then drain limited batch).
/// </summary>
public sealed class FilePipelineBackgroundService(
    IFilePoller poller,
    IFileEventQueue queue,
    Core.IFileProcessingService processingService,
    ILogger<FilePipelineBackgroundService> logger,
    IOptionsMonitor<PollingOptions> pollingOptions,
    IOptionsMonitor<PipelineFeaturesOptions> featureOptions) : BackgroundService
{
    private readonly IFilePoller _poller = poller;
    private readonly IFileEventQueue _queue = queue;
    private readonly Core.IFileProcessingService _processingService = processingService;
    private readonly ILogger<FilePipelineBackgroundService> _logger = logger;
    private readonly IOptionsMonitor<PollingOptions> _options = pollingOptions;
    private readonly IOptionsMonitor<PipelineFeaturesOptions> _featureOptions = featureOptions;

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
        var features = _featureOptions.CurrentValue;

        _logger.LogDebug("Pipeline cycle start (interval {Interval}ms)", options.IntervalMilliseconds);

        // Poll phase
        if (features?.EnablePolling == true)
        {
            var pollResult = await _poller.PollAsync(ct).ConfigureAwait(false);
            if (pollResult.IsFailure)
            {
                _logger.LogWarning("Polling failed: {Error}", pollResult.Error);
            }
        }
        else
        {
            _logger.LogTrace("Polling disabled via feature flags - skipping poll cycle");
        }

        // Processing phase
        if (features?.EnableProcessing != true)
        {
            _logger.LogTrace("Processing disabled via feature flags - skipping processing cycle");
            return;
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
