using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class FileProcessingOrchestratorFailureTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
        where T : class, new()
    {
        private readonly T _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task ProcessAsync_MissingSourceFile_PropagatesFailure()
    {
        // Arrange: source file path that does not exist
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.txt");
        var destRoot = Path.Combine(Path.GetTempPath(), "orchestrator-fail-tests", Guid.NewGuid().ToString("N"));

        var ev = new FileEvent(
            Id: Guid.NewGuid().ToString("N"),
            Metadata: new FileMetadata(missingPath, 0, DateTimeOffset.UtcNow, "none", null),
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
        var orchestrator = new FileProcessingOrchestrator(
            router,
            readers,
            sinks,
            new StaticOptionsMonitor<DestinationsOptions>(destinations),
            new StaticOptionsMonitor<IdempotencyOptions>(new IdempotencyOptions { Enabled = false }),
            new StaticOptionsMonitor<RemoteFileSourcesOptions>(remoteSources),
            new Infrastructure.Idempotency.InMemoryIdempotencyStore(),
            new Infrastructure.Notifications.StubFileProcessedNotifier(
                options: new StaticOptionsMonitor<ServiceBusNotificationOptions>(new ServiceBusNotificationOptions { Enabled = false }),
                idempotencyStore: new Infrastructure.Idempotency.InMemoryIdempotencyStore(),
                telemetry: new Infrastructure.Telemetry.FileProcessingTelemetry(),
                logger: NullLogger<Infrastructure.Notifications.StubFileProcessedNotifier>.Instance
            ),
            new Infrastructure.Telemetry.FileProcessingTelemetry(),
            new Infrastructure.Remote.SshNetSftpClientFactory(NullLogger<Infrastructure.Remote.SshNetSftpClientFactory>.Instance),
            new Infrastructure.Secrets.InMemorySecretResolver(NullLogger<Infrastructure.Secrets.InMemorySecretResolver>.Instance),
            NullLogger<Infrastructure.Remote.SftpRemoteFileClient>.Instance,
            NullLogger<Infrastructure.Remote.FtpRemoteFileClient>.Instance,
            NullLogger<FileProcessingOrchestrator>.Instance);

        // Act
        var res = await orchestrator.ProcessAsync(ev, CancellationToken.None);

        // Assert
        Assert.True(res.IsFailure);

        // Cleanup
        try { Directory.Delete(destRoot, true); } catch { }
    }
}
