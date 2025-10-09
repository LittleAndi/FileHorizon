using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using FileHorizon.Application.Models;
using FileHorizon.Application.Models.Notifications;
using FileHorizon.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class FileProcessingOrchestratorNotifierTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class, new()
    {
        private readonly T _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class CapturingNotifier : IFileProcessedNotifier
    {
        public List<FileProcessedNotification> Published { get; } = new();
        public Task<Result> PublishAsync(FileProcessedNotification notification, CancellationToken ct)
        {
            Published.Add(notification);
            return Task.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task SuccessPublish_InvokesNotifierOnce()
    {
        var srcFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        var destRoot = Path.Combine(Path.GetTempPath(), "orchestrator-notifier-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(srcFile)!);
        await File.WriteAllTextAsync(srcFile, "hello world");

        var ev = new FileEvent(
            Id: Guid.NewGuid().ToString("N"),
            Metadata: new FileMetadata(srcFile, new FileInfo(srcFile).Length, DateTimeOffset.UtcNow, "none", null),
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: "local",
            DestinationPath: string.Empty,
            DeleteAfterTransfer: false);

        var routing = new RoutingOptions
        {
            Rules =
            [
                new RoutingRuleOptions
                {
                    Name = "local",
                    Protocol = "local",
                    PathGlob = "**/*.txt",
                    Destinations = ["OutboxA"],
                    Overwrite = true,
                    RenamePattern = "{fileName}"
                }
            ]
        };
        var router = new Infrastructure.Processing.SimpleFileRouter(new StaticOptionsMonitor<RoutingOptions>(routing), NullLogger<Infrastructure.Processing.SimpleFileRouter>.Instance);

        var readers = new List<IFileContentReader> { new Infrastructure.Processing.LocalFileContentReader(NullLogger<Infrastructure.Processing.LocalFileContentReader>.Instance) };
        var sinks = new List<IFileSink> { new Infrastructure.Processing.LocalFileSink(NullLogger<Infrastructure.Processing.LocalFileSink>.Instance) };
        var destinations = new DestinationsOptions
        {
            Local =
            [
                new LocalDestinationOptions { Name = "OutboxA", RootPath = destRoot }
            ]
        };
        var remoteSources = new RemoteFileSourcesOptions();
        var notifier = new CapturingNotifier();
        var orchestrator = new FileProcessingOrchestrator(
            router,
            readers,
            sinks,
            new StaticOptionsMonitor<DestinationsOptions>(destinations),
            new StaticOptionsMonitor<IdempotencyOptions>(new IdempotencyOptions { Enabled = false }),
            new StaticOptionsMonitor<RemoteFileSourcesOptions>(remoteSources),
            new Infrastructure.Idempotency.InMemoryIdempotencyStore(),
            notifier,
            new Infrastructure.Remote.SshNetSftpClientFactory(NullLogger<Infrastructure.Remote.SshNetSftpClientFactory>.Instance),
            new Infrastructure.Secrets.InMemorySecretResolver(NullLogger<Infrastructure.Secrets.InMemorySecretResolver>.Instance),
            NullLogger<Infrastructure.Remote.SftpRemoteFileClient>.Instance,
            NullLogger<Infrastructure.Remote.FtpRemoteFileClient>.Instance,
            NullLogger<FileProcessingOrchestrator>.Instance);

        var res = await orchestrator.ProcessAsync(ev, CancellationToken.None);
        Assert.True(res.IsSuccess);
        Assert.Single(notifier.Published);
        Assert.Equal(ev.Metadata.SourcePath, notifier.Published[0].FullPath);
        Assert.Equal(ev.Id, notifier.Published[0].IdempotencyKey);

        // Cleanup
        try { File.Delete(srcFile); } catch { }
        try { Directory.Delete(destRoot, true); } catch { }
    }
}
