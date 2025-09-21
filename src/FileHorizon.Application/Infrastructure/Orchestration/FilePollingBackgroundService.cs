using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Orchestration;

/// <summary>
/// Background service responsible only for polling new file events and enqueuing them.
/// </summary>
public sealed class FilePollingBackgroundService(
    IFilePoller poller,
    IOptionsMonitor<PollingOptions> pollingOptions,
    ILogger<FilePollingBackgroundService> logger) : BackgroundService
{
    private readonly IFilePoller _poller = poller;
    private readonly IOptionsMonitor<PollingOptions> _pollingOptions = pollingOptions;
    private readonly ILogger<FilePollingBackgroundService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File polling service starting");
        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStart = DateTimeOffset.UtcNow;
            var options = _pollingOptions.CurrentValue;
            try
            {
                var pollResult = await _poller.PollAsync(stoppingToken).ConfigureAwait(false);
                if (pollResult.IsFailure)
                {
                    _logger.LogWarning("Polling failed: {Error}", pollResult.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in polling loop");
                var backoff = Math.Min(2000, options.IntervalMilliseconds);
                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
            }

            var elapsedMs = (int)(DateTimeOffset.UtcNow - cycleStart).TotalMilliseconds;
            var remaining = _pollingOptions.CurrentValue.IntervalMilliseconds - elapsedMs;
            if (remaining > 0)
            {
                await Task.Delay(remaining, stoppingToken).ConfigureAwait(false);
            }
            else if (elapsedMs > options.IntervalMilliseconds)
            {
                _logger.LogWarning("Polling cycle overran interval: elapsed {Elapsed}ms > interval {Interval}ms", elapsedMs, options.IntervalMilliseconds);
            }
        }

        _logger.LogInformation("File polling service stopping");
    }
}
