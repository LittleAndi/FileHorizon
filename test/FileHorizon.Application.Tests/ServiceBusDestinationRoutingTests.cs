using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Infrastructure.Remote;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FileHorizon.Application.Tests;

public class ServiceBusDestinationRoutingTests
{
    private static IOptionsMonitor<RoutingOptions> RoutingOptions(params RoutingRuleOptions[] rules)
    {
        var ro = new RoutingOptions { Rules = [.. rules] };
        var monitor = Substitute.For<IOptionsMonitor<RoutingOptions>>();
        monitor.CurrentValue.Returns(ro);
        return monitor;
    }

    private static IOptionsMonitor<DestinationsOptions> DestinationsOptions(params ServiceBusDestinationOptions[] serviceBus)
    {
        var d = new DestinationsOptions { ServiceBus = [.. serviceBus] };
        var monitor = Substitute.For<IOptionsMonitor<DestinationsOptions>>();
        monitor.CurrentValue.Returns(d);
        return monitor;
    }

    [Fact]
    public async Task Router_Resolves_ServiceBus_DestinationKind()
    {
        var routing = RoutingOptions(new RoutingRuleOptions
        {
            Name = "sbRule",
            Protocol = "local",
            PathGlob = "**/*.txt",
            Destinations = ["events"]
        });
        var destinations = DestinationsOptions(new ServiceBusDestinationOptions
        {
            Name = "events",
            EntityName = "events",
            IsTopic = false
        });
        var logger = Substitute.For<ILogger<SimpleFileRouter>>();
        var router = new SimpleFileRouter(routing, destinations, logger);
        var fe = new FileEvent(
            Id: "id1",
            Metadata: new FileMetadata(SourcePath: "C:/data/in/file1.txt", SizeBytes: 10, LastModifiedUtc: DateTimeOffset.UtcNow, HashAlgorithm: "MD5", Checksum: null),
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: "local",
            DestinationPath: "C:/data/out/file1.txt",
            DeleteAfterTransfer: false);
        var result = await router.RouteAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.Equal(DestinationKind.ServiceBus, result.Value![0].Kind);
    }

    private sealed class FakePublisher : IFileContentPublisher
    {
        public bool Called { get; private set; }
        public FilePublishRequest? LastRequest { get; private set; }
        public Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct)
        {
            Called = true;
            LastRequest = request;
            return Task.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task Orchestrator_Calls_Publisher_For_ServiceBus_Destination()
    {
        // Arrange routing to return service bus destination
        var routing = RoutingOptions(new RoutingRuleOptions
        {
            Name = "sbRule",
            Protocol = "local",
            PathGlob = "**/*.log",
            Destinations = ["events"]
        });
        var destinations = DestinationsOptions(new ServiceBusDestinationOptions
        {
            Name = "events",
            EntityName = "events",
            IsTopic = false
        });
        var routerLogger = Substitute.For<ILogger<SimpleFileRouter>>();
        var router = new SimpleFileRouter(routing, destinations, routerLogger);

        var reader = Substitute.For<IFileContentReader>();
        reader.OpenReadAsync(Arg.Any<FileReference>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(Result<Stream>.Success(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello")))));

        var sink = Substitute.For<IFileSink>();
        sink.Name.Returns("Local");
        var idempStore = Substitute.For<IIdempotencyStore>();
        idempStore.TryMarkProcessedAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var remoteSources = Substitute.For<IOptionsMonitor<RemoteFileSourcesOptions>>();
        remoteSources.CurrentValue.Returns(new RemoteFileSourcesOptions());
        var idempOpts = Substitute.For<IOptionsMonitor<IdempotencyOptions>>();
        idempOpts.CurrentValue.Returns(new IdempotencyOptions { Enabled = false });
        var destOpts = destinations;
        var sftpFactory = Substitute.For<ISftpClientFactory>();
        var secretResolver = Substitute.For<ISecretResolver>();
        var sftpLogger = Substitute.For<ILogger<SftpRemoteFileClient>>();
        var ftpLogger = Substitute.For<ILogger<FtpRemoteFileClient>>();
        var orchestratorLogger = Substitute.For<ILogger<FileProcessingOrchestrator>>();
        var publisher = new FakePublisher();
        var sbOpts = Substitute.For<IOptionsMonitor<ServiceBusPublisherOptions>>();
        sbOpts.CurrentValue.Returns(new ServiceBusPublisherOptions
        {
            ConnectionString = "Endpoint=sb://dummy.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE=",
            MaxConcurrentPublishes = 1,
            EnableTracing = false
        });
        // Publisher is fake; Azure client unused but options must exist for other registrations (not building DI here)
        var orchestrator = new FileProcessingOrchestrator(router, [reader], [sink], destOpts, idempOpts, remoteSources, idempStore, sftpFactory, secretResolver, sftpLogger, ftpLogger, publisher, orchestratorLogger);
        var fe = new FileEvent(
            Id: "id2",
            Metadata: new FileMetadata(SourcePath: "C:/data/in/app.log", SizeBytes: 5, LastModifiedUtc: DateTimeOffset.UtcNow, HashAlgorithm: "MD5", Checksum: null),
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: "local",
            DestinationPath: "C:/data/out/app.log",
            DeleteAfterTransfer: false);

        // Act
        var result = await orchestrator.ProcessAsync(fe, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(publisher.Called);
        Assert.NotNull(publisher.LastRequest);
        Assert.Equal("events", publisher.LastRequest!.DestinationName);
        Assert.Equal("app.log", publisher.LastRequest!.FileName);
    }
}
