using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Delegates polling to any registered protocol-specific pollers.
/// </summary>
public sealed class MultiProtocolPoller(IEnumerable<IFilePoller> pollers, ILogger<MultiProtocolPoller> logger) : IFilePoller
{
    private readonly IEnumerable<IFilePoller> _pollers = [.. pollers.Where(p => p is not MultiProtocolPoller)];
    private readonly ILogger<MultiProtocolPoller> _logger = logger;

    public async Task<Result> PollAsync(CancellationToken ct)
    {
        foreach (var poller in _pollers)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await poller.PollAsync(ct).ConfigureAwait(false);
                if (r.IsFailure)
                {
                    _logger.LogWarning("Poller {Poller} failed: {Error}", poller.GetType().Name, r.Error);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in poller {Poller}", poller.GetType().Name);
            }
        }
        return Result.Success();
    }
}
