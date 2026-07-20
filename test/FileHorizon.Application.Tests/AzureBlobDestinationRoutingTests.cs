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

public class AzureBlobDestinationRoutingTests
{
    private static IOptionsMonitor<RoutingOptions> RoutingOptions(params RoutingRuleOptions[] rules)
    {
        var ro = new RoutingOptions { Rules = [.. rules] };
        var monitor = Substitute.For<IOptionsMonitor<RoutingOptions>>();
        monitor.CurrentValue.Returns(ro);
        return monitor;
    }

    private static IOptionsMonitor<DestinationsOptions> DestinationsOptions(params AzureBlobDestinationOptions[] azureBlob)
    {
        var d = new DestinationsOptions { AzureBlob = [.. azureBlob] };
        var monitor = Substitute.For<IOptionsMonitor<DestinationsOptions>>();
        monitor.CurrentValue.Returns(d);
        return monitor;
    }

    private static AzureBlobDestinationOptions BlobDestination(string name = "analytics-blob") => new()
    {
        Name = name,
        ContainerName = "incoming",
        BlobTechnical = new AzureBlobTechnicalOptions { AccountName = "myaccount" }
    };

    [Fact]
    public async Task Router_Resolves_AzureBlob_DestinationKind()
    {
        var routing = RoutingOptions(new RoutingRuleOptions
        {
            Name = "blobRule",
            Protocol = "local",
            PathGlob = "**/*.txt",
            Destinations = ["analytics-blob"]
        });
        var destinations = DestinationsOptions(BlobDestination());
        var router = new SimpleFileRouter(routing, destinations, Substitute.For<ILogger<SimpleFileRouter>>());
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
        Assert.Equal(DestinationKind.AzureBlob, result.Value![0].Kind);
    }

    private sealed class FakeBlobSink : IFileSink
    {
        public FileReference? LastTarget { get; private set; }
        public string Name => AzureBlobFileSink.SinkName;
        public Task<Result> WriteAsync(FileReference target, Stream content, FileWriteOptions options, CancellationToken ct)
        {
            LastTarget = target;
            return Task.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task Orchestrator_Dispatches_To_BlobSink_For_AzureBlob_Destination()
    {
        var routing = RoutingOptions(new RoutingRuleOptions
        {
            Name = "blobRule",
            Protocol = "local",
            PathGlob = "**/*.log",
            Destinations = ["analytics-blob"]
        });
        var destinations = DestinationsOptions(BlobDestination());
        var router = new SimpleFileRouter(routing, destinations, Substitute.For<ILogger<SimpleFileRouter>>());

        var reader = Substitute.For<IFileContentReader>();
        reader.OpenReadAsync(Arg.Any<FileReference>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(Result<Stream>.Success(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello")))));

        var blobSink = new FakeBlobSink();
        var idempStore = Substitute.For<IIdempotencyStore>();
        var remoteSources = Substitute.For<IOptionsMonitor<RemoteFileSourcesOptions>>();
        remoteSources.CurrentValue.Returns(new RemoteFileSourcesOptions());
        var idempOpts = Substitute.For<IOptionsMonitor<IdempotencyOptions>>();
        idempOpts.CurrentValue.Returns(new IdempotencyOptions { Enabled = false });
        var publisher = Substitute.For<IFileContentPublisher>();
        var orchestrator = new FileProcessingOrchestrator(
            router,
            [reader],
            [blobSink],
            destinations,
            idempOpts,
            remoteSources,
            idempStore,
            Substitute.For<ISftpClientFactory>(),
            Substitute.For<ISecretResolver>(),
            Substitute.For<ILogger<SftpRemoteFileClient>>(),
            Substitute.For<ILogger<FtpRemoteFileClient>>(),
            publisher,
            new ExtensionFileTypeDetector(),
            Substitute.For<ILogger<FileProcessingOrchestrator>>());
        var fe = new FileEvent(
            Id: "id2",
            Metadata: new FileMetadata(SourcePath: "C:/data/in/app.log", SizeBytes: 5, LastModifiedUtc: DateTimeOffset.UtcNow, HashAlgorithm: "MD5", Checksum: null),
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: "local",
            DestinationPath: "C:/data/out/app.log",
            DeleteAfterTransfer: false);

        var result = await orchestrator.ProcessAsync(fe, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(blobSink.LastTarget);
        Assert.Equal(AzureBlobFileSink.Scheme, blobSink.LastTarget!.Scheme);
        Assert.Equal("analytics-blob", blobSink.LastTarget.SourceName);
        Assert.Equal("app.log", blobSink.LastTarget.Path);
        await publisher.DidNotReceive().PublishAsync(Arg.Any<FilePublishRequest>(), Arg.Any<CancellationToken>());
    }
}
