using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Delegates polling to either the synthetic poller or the local directory poller based on feature flags.
/// Keeps a stable IFilePoller registration while allowing runtime switching.
/// </summary>
public sealed class PollerSelector : IFilePoller
{
    private readonly SyntheticFilePoller _synthetic;
    private readonly LocalDirectoryPoller _local;
    private readonly IOptionsMonitor<PipelineFeaturesOptions> _featureOptions;
    private readonly ILogger<PollerSelector> _logger;

    public PollerSelector(
        SyntheticFilePoller synthetic,
        LocalDirectoryPoller local,
        IOptionsMonitor<PipelineFeaturesOptions> featureOptions,
        ILogger<PollerSelector> logger)
    {
        _synthetic = synthetic;
        _local = local;
        _featureOptions = featureOptions;
        _logger = logger;
    }

    public Task<Result> PollAsync(CancellationToken ct)
    {
        var features = _featureOptions.CurrentValue;
        if (features is not null && features.UseSyntheticPoller)
        {
            _logger.LogTrace("Using synthetic poller");
            return _synthetic.PollAsync(ct);
        }

        _logger.LogTrace("Using local directory poller");
        return _local.PollAsync(ct);
    }
}
