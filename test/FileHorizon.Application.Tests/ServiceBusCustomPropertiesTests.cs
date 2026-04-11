using System.Text;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class ServiceBusCustomPropertiesTests
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

    private sealed class CapturingPublisher : IFileContentPublisher
    {
        public FilePublishRequest? LastRequest { get; private set; }
        public bool WasCalled { get; private set; }

        public Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct)
        {
            WasCalled = true;
            LastRequest = request;
            return Task.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task Orchestrator_MergesConfiguredApplicationProperties_WithRuntimeProperties()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.txt");
        var content = "test content for custom properties";
        await File.WriteAllTextAsync(tempFile, content);

        try
        {
            var publisher = new CapturingPublisher();
            var destinations = new DestinationsOptions
            {
                ServiceBus =
                [
                    new ServiceBusDestinationOptions
                    {
                        Name = "TestQueue",
                        EntityName = "test-queue",
                        IsTopic = false,
                        ContentType = "text/plain",
                        ApplicationProperties = new Dictionary<string, string>
                        {
                            ["correlationId"] = "test-correlation-123",
                            ["customLabel"] = "integration-test",
                            ["environment"] = "dev"
                        }
                    }
                ]
            };

            var routing = new RoutingOptions
            {
                Rules =
                [
                    new RoutingRuleOptions
                    {
                        Name = "TestRule",
                        Protocol = "local",
                        PathGlob = "**/*.txt",
                        Destinations = ["TestQueue"]
                    }
                ]
            };

            var router = new Infrastructure.Processing.SimpleFileRouter(
                routingOptions: new StaticOptionsMonitor<RoutingOptions>(routing),
                destinationsOptions: new StaticOptionsMonitor<DestinationsOptions>(destinations),
                logger: NullLogger<Infrastructure.Processing.SimpleFileRouter>.Instance
            );

            var readers = new List<IFileContentReader>
            {
                new Infrastructure.Processing.LocalFileContentReader(NullLogger<Infrastructure.Processing.LocalFileContentReader>.Instance)
            };

            var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

            var orchestrator = new FileProcessingOrchestrator(
                router: router,
                readers: readers,
                sinks: new List<IFileSink>(),
                destinations: new StaticOptionsMonitor<DestinationsOptions>(destinations),
                idempotencyOptions: new StaticOptionsMonitor<IdempotencyOptions>(new IdempotencyOptions { Enabled = false }),
                remoteSources: new StaticOptionsMonitor<RemoteFileSourcesOptions>(new RemoteFileSourcesOptions()),
                idempotencyStore: new Infrastructure.Idempotency.InMemoryIdempotencyStore(),
                sftpFactory: new Infrastructure.Remote.SshNetSftpClientFactory(NullLogger<Infrastructure.Remote.SshNetSftpClientFactory>.Instance),
                secretResolver: new Infrastructure.Secrets.InMemorySecretResolver(configuration, NullLogger<Infrastructure.Secrets.InMemorySecretResolver>.Instance),
                sftpClientLogger: NullLogger<Infrastructure.Remote.SftpRemoteFileClient>.Instance,
                ftpClientLogger: NullLogger<Infrastructure.Remote.FtpRemoteFileClient>.Instance,
                publisher: publisher,
                fileTypeDetector: new Infrastructure.Processing.ExtensionFileTypeDetector(),
                logger: NullLogger<FileProcessingOrchestrator>.Instance
            );

            var fileEvent = new FileEvent(
                Id: Guid.NewGuid().ToString("N"),
                Metadata: new FileMetadata(tempFile, new FileInfo(tempFile).Length, DateTimeOffset.UtcNow, "none", null),
                DiscoveredAtUtc: DateTimeOffset.UtcNow,
                Protocol: "local",
                DestinationPath: string.Empty,
                DeleteAfterTransfer: false
            );

            // Act
            var result = await orchestrator.ProcessAsync(fileEvent, CancellationToken.None);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.True(publisher.WasCalled);
            Assert.NotNull(publisher.LastRequest);
            Assert.NotNull(publisher.LastRequest.ApplicationProperties);

            // Verify configured properties are present
            Assert.Equal("test-correlation-123", publisher.LastRequest.ApplicationProperties["correlationId"]);
            Assert.Equal("integration-test", publisher.LastRequest.ApplicationProperties["customLabel"]);
            Assert.Equal("dev", publisher.LastRequest.ApplicationProperties["environment"]);

            // Verify runtime properties are also present
            Assert.Equal(fileEvent.Id, publisher.LastRequest.ApplicationProperties["fh.fileId"]);
            Assert.Equal("local", publisher.LastRequest.ApplicationProperties["fh.protocol"]);

            // Verify there are 5 total properties (3 configured + 2 runtime)
            Assert.Equal(5, publisher.LastRequest.ApplicationProperties.Count);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task Orchestrator_WithNoConfiguredProperties_OnlyAddsRuntimeProperties()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, "test");

        try
        {
            var publisher = new CapturingPublisher();
            var destinations = new DestinationsOptions
            {
                ServiceBus =
                [
                    new ServiceBusDestinationOptions
                    {
                        Name = "TestQueue",
                        EntityName = "test-queue",
                        ApplicationProperties = null // No configured properties
                    }
                ]
            };

            var routing = new RoutingOptions
            {
                Rules =
                [
                    new RoutingRuleOptions
                    {
                        Name = "TestRule",
                        Protocol = "local",
                        PathGlob = "**/*.txt",
                        Destinations = ["TestQueue"]
                    }
                ]
            };

            var router = new Infrastructure.Processing.SimpleFileRouter(
                routingOptions: new StaticOptionsMonitor<RoutingOptions>(routing),
                destinationsOptions: new StaticOptionsMonitor<DestinationsOptions>(destinations),
                logger: NullLogger<Infrastructure.Processing.SimpleFileRouter>.Instance
            );

            var readers = new List<IFileContentReader>
            {
                new Infrastructure.Processing.LocalFileContentReader(NullLogger<Infrastructure.Processing.LocalFileContentReader>.Instance)
            };

            var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

            var orchestrator = new FileProcessingOrchestrator(
                router: router,
                readers: readers,
                sinks: new List<IFileSink>(),
                destinations: new StaticOptionsMonitor<DestinationsOptions>(destinations),
                idempotencyOptions: new StaticOptionsMonitor<IdempotencyOptions>(new IdempotencyOptions { Enabled = false }),
                remoteSources: new StaticOptionsMonitor<RemoteFileSourcesOptions>(new RemoteFileSourcesOptions()),
                idempotencyStore: new Infrastructure.Idempotency.InMemoryIdempotencyStore(),
                sftpFactory: new Infrastructure.Remote.SshNetSftpClientFactory(NullLogger<Infrastructure.Remote.SshNetSftpClientFactory>.Instance),
                secretResolver: new Infrastructure.Secrets.InMemorySecretResolver(configuration, NullLogger<Infrastructure.Secrets.InMemorySecretResolver>.Instance),
                sftpClientLogger: NullLogger<Infrastructure.Remote.SftpRemoteFileClient>.Instance,
                ftpClientLogger: NullLogger<Infrastructure.Remote.FtpRemoteFileClient>.Instance,
                publisher: publisher,
                fileTypeDetector: new Infrastructure.Processing.ExtensionFileTypeDetector(),
                logger: NullLogger<FileProcessingOrchestrator>.Instance
            );

            var fileEvent = new FileEvent(
                Id: Guid.NewGuid().ToString("N"),
                Metadata: new FileMetadata(tempFile, new FileInfo(tempFile).Length, DateTimeOffset.UtcNow, "none", null),
                DiscoveredAtUtc: DateTimeOffset.UtcNow,
                Protocol: "local",
                DestinationPath: string.Empty,
                DeleteAfterTransfer: false
            );

            // Act
            var result = await orchestrator.ProcessAsync(fileEvent, CancellationToken.None);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.True(publisher.WasCalled);
            Assert.NotNull(publisher.LastRequest);
            Assert.NotNull(publisher.LastRequest.ApplicationProperties);

            // Only runtime properties should be present
            Assert.Equal(2, publisher.LastRequest.ApplicationProperties.Count);
            Assert.Equal(fileEvent.Id, publisher.LastRequest.ApplicationProperties["fh.fileId"]);
            Assert.Equal("local", publisher.LastRequest.ApplicationProperties["fh.protocol"]);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
