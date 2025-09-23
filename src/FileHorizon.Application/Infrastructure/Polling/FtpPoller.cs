using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Remote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Polling;

public sealed class FtpPoller(IFileEventQueue queue,
    IOptionsMonitor<RemoteFileSourcesOptions> remoteOptions,
    ILogger<FtpPoller> logger,
    ILoggerFactory loggerFactory,
    FileHorizon.Application.Abstractions.ISecretResolver secretResolver) : RemotePollerBase(queue, remoteOptions, logger)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IOptionsMonitor<RemoteFileSourcesOptions> _remoteOptions = remoteOptions;
    private readonly FileHorizon.Application.Abstractions.ISecretResolver _secretResolver = secretResolver;

    protected override List<IRemoteFileSourceDescriptor> GetEnabledSources()
    {
        var list = new List<IRemoteFileSourceDescriptor>();
        foreach (var s in _remoteOptions.CurrentValue.Ftp)
        {
            if (!s.Enabled) continue;
            list.Add(new FtpSourceDescriptor(s));
        }
        return list;
    }

    protected override IRemoteFileClient CreateClient(IRemoteFileSourceDescriptor source)
    {
        var s = ((FtpSourceDescriptor)source).Options;
        string? password = null;
        if (!string.IsNullOrWhiteSpace(s.PasswordSecretRef))
        {
            // Best effort sync wait kept minimal because remote poll cycle is already async. We design secret resolution to be fast/cached.
            password = _secretResolver.ResolveSecretAsync(s.PasswordSecretRef).GetAwaiter().GetResult();
        }
        return new FtpRemoteFileClient(_loggerFactory.CreateLogger<FtpRemoteFileClient>(), s.Host, s.Port, s.Username, password, s.Passive);
    }

    protected override ProtocolType MapProtocolType(ProtocolType protocol) => ProtocolType.Ftp;

    private sealed class FtpSourceDescriptor(FtpSourceOptions options) : IRemoteFileSourceDescriptor
    {
        public FtpSourceOptions Options { get; } = options;
        public string Name => Options.Name;
        public string RemotePath => Options.RemotePath;
        public string Pattern => Options.Pattern;
        public bool Recursive => Options.Recursive;
        public int MinStableSeconds => Options.MinStableSeconds;
        public string? DestinationPath => Options.DestinationPath;
        public string Host => Options.Host;
        public int Port => Options.Port;
    }
}
