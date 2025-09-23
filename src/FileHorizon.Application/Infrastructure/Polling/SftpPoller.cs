using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Remote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Infrastructure.Polling;

public sealed class SftpPoller(IFileEventQueue queue,
    IOptionsMonitor<RemoteFileSourcesOptions> remoteOptions,
    ILogger<SftpPoller> logger,
    ILoggerFactory loggerFactory,
    FileHorizon.Application.Abstractions.ISecretResolver secretResolver) : RemotePollerBase(queue, remoteOptions, logger)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IOptionsMonitor<RemoteFileSourcesOptions> _remoteOptions = remoteOptions;
    private readonly FileHorizon.Application.Abstractions.ISecretResolver _secretResolver = secretResolver;

    protected override List<IRemoteFileSourceDescriptor> GetEnabledSources()
    {
        var list = new List<IRemoteFileSourceDescriptor>();
        foreach (var s in _remoteOptions.CurrentValue.Sftp)
        {
            if (!s.Enabled) continue;
            list.Add(new SftpSourceDescriptor(s));
        }
        return list;
    }

    protected override IRemoteFileClient CreateClient(IRemoteFileSourceDescriptor source)
    {
        var s = ((SftpSourceDescriptor)source).Options;
        string? password = null;
        string? privateKey = null;
        string? passphrase = null;
        if (!string.IsNullOrWhiteSpace(s.PasswordSecretRef))
        {
            password = _secretResolver.ResolveSecretAsync(s.PasswordSecretRef).GetAwaiter().GetResult();
        }
        if (!string.IsNullOrWhiteSpace(s.PrivateKeySecretRef))
        {
            privateKey = _secretResolver.ResolveSecretAsync(s.PrivateKeySecretRef).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(s.PrivateKeyPassphraseSecretRef))
            {
                passphrase = _secretResolver.ResolveSecretAsync(s.PrivateKeyPassphraseSecretRef).GetAwaiter().GetResult();
            }
        }
        return new SftpRemoteFileClient(
            _loggerFactory.CreateLogger<SftpRemoteFileClient>(),
            s.Host,
            s.Port,
            s.Username ?? string.Empty,
            password,
            privateKey,
            passphrase);
    }

    protected override ProtocolType MapProtocolType(ProtocolType protocol) => ProtocolType.Sftp;

    private sealed class SftpSourceDescriptor(SftpSourceOptions options) : IRemoteFileSourceDescriptor
    {
        public SftpSourceOptions Options { get; } = options;
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
