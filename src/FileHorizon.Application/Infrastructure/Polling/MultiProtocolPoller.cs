using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Delegates polling, and folder listing for the route inventory, to any registered protocol-specific pollers.
/// </summary>
public sealed class MultiProtocolPoller(IEnumerable<IFilePoller> pollers, ILogger<MultiProtocolPoller> logger) : IFilePoller, ISourceFolderLister
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

    public async Task<IReadOnlyList<SourceFolderListing>> ListSourceFoldersAsync(CancellationToken ct)
    {
        var listings = new List<SourceFolderListing>();
        foreach (var lister in _pollers.OfType<ISourceFolderLister>())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                listings.AddRange(await lister.ListSourceFoldersAsync(ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Isolated like PollAsync: one poller's failure must not hide the other pollers' sources.
                _logger.LogError(ex, "Folder listing failed in poller {Poller}", lister.GetType().Name);
            }
        }
        return listings;
    }
}
