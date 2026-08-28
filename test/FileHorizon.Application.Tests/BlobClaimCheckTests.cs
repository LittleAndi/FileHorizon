using System.Text.Json;
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

/// <summary>
/// Covers the claim-check post-write notification on an Azure Blob destination: after a successful upload,
/// a pointer to the blob is published to Service Bus instead of the file content.
/// </summary>
public class BlobClaimCheckTests
{
    private const string BlobUri = "https://myaccount.blob.core.windows.net/inbox/despatchadvice/app.log";

    private sealed class FakeBlobSink(FileWriteReceipt receipt) : IFileSink
    {
        public string Name => AzureBlobFileSink.SinkName;
        public int WriteCount { get; private set; }
        public Task<Result<FileWriteReceipt>> WriteAsync(FileReference target, Stream content, FileWriteOptions options, CancellationToken ct)
        {
            WriteCount++;
            return Task.FromResult(Result<FileWriteReceipt>.Success(receipt));
        }
    }

    private static AzureBlobDestinationOptions BlobDestination(string? claimCheckTarget)
        => new()
        {
            Name = "inbox-blob",
            ContainerName = "inbox",
            BlobTechnical = new AzureBlobTechnicalOptions { ConnectionString = "UseDevelopmentStorage=true" },
            ClaimCheck = claimCheckTarget is null ? null : new BlobClaimCheckOptions { ServiceBusDestination = claimCheckTarget }
        };

    private static ServiceBusDestinationOptions ServiceBusDestination(Action<ServiceBusDestinationOptions>? configure = null)
    {
        var d = new ServiceBusDestinationOptions
        {
            Name = "despatchadvice-queue",
            EntityName = "despatchadvice-sbq",
            ServiceBusTechnical = new ServiceBusTechnicalOptions { ConnectionString = "Endpoint=sb://fake/" }
        };
        configure?.Invoke(d);
        return d;
    }

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private static FileEvent Event() => new(
        Id: "file-1",
        Metadata: new FileMetadata(SourcePath: "C:/data/in/app.log", SizeBytes: 5, LastModifiedUtc: DateTimeOffset.UtcNow, HashAlgorithm: "MD5", Checksum: null),
        DiscoveredAtUtc: DateTimeOffset.UtcNow,
        Protocol: "local",
        DestinationPath: "C:/data/out/app.log",
        DeleteAfterTransfer: false);

    private static (FileProcessingOrchestrator Orchestrator, FakeBlobSink Sink) BuildOrchestrator(
        DestinationsOptions destinationsOptions,
        IFileContentPublisher publisher,
        FileWriteReceipt? receipt = null)
    {
        var destinations = Monitor(destinationsOptions);
        var routing = Monitor(new RoutingOptions
        {
            Rules =
            [
                new RoutingRuleOptions
                {
                    Name = "logsToInbox",
                    Protocol = "local",
                    PathGlob = "**/*.log",
                    Destinations = ["inbox-blob"]
                }
            ]
        });
        var router = new SimpleFileRouter(routing, destinations, Substitute.For<ILogger<SimpleFileRouter>>());

        var reader = Substitute.For<IFileContentReader>();
        reader.OpenReadAsync(Arg.Any<FileReference>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Result<Stream>.Success(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello")))));

        var sink = new FakeBlobSink(receipt ?? new FileWriteReceipt(5, new Uri(BlobUri), "text/plain"));

        var orchestrator = new FileProcessingOrchestrator(
            router,
            [reader],
            [sink],
            destinations,
            Monitor(new IdempotencyOptions { Enabled = false }),
            Monitor(new RemoteFileSourcesOptions()),
            Substitute.For<IIdempotencyStore>(),
            Substitute.For<ISftpClientFactory>(),
            Substitute.For<ISecretResolver>(),
            Substitute.For<ILogger<SftpRemoteFileClient>>(),
            Substitute.For<ILogger<FtpRemoteFileClient>>(),
            publisher,
            new ExtensionFileTypeDetector(),
            Substitute.For<ILogger<FileProcessingOrchestrator>>());
        return (orchestrator, sink);
    }

    private static IFileContentPublisher CapturingPublisher(out List<FilePublishRequest> captured, Result? result = null)
    {
        var requests = new List<FilePublishRequest>();
        captured = requests;
        var publisher = Substitute.For<IFileContentPublisher>();
        publisher.PublishAsync(Arg.Any<FilePublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                requests.Add(ci.Arg<FilePublishRequest>());
                return Task.FromResult(result ?? Result.Success());
            });
        return publisher;
    }

    [Fact]
    public async Task ClaimCheck_Publishes_Pointer_Envelope_After_Blob_Write()
    {
        var publisher = CapturingPublisher(out var requests);
        var (orchestrator, sink) = BuildOrchestrator(
            new DestinationsOptions
            {
                AzureBlob = [BlobDestination("despatchadvice-queue")],
                ServiceBus = [ServiceBusDestination()]
            },
            publisher);

        var result = await orchestrator.ProcessAsync(Event(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, sink.WriteCount);
        var request = Assert.Single(requests);
        Assert.Equal("despatchadvice-queue", request.DestinationName);

        var envelope = JsonSerializer.Deserialize<JsonElement>(request.Content.Span);
        Assert.Equal(BlobUri, envelope.GetProperty("blobUrl").GetString());
        Assert.Equal("text/plain", envelope.GetProperty("contentType").GetString());
        Assert.Equal(5, envelope.GetProperty("length").GetInt64());
    }

    [Fact]
    public async Task ClaimCheck_Message_Carries_Marker_And_Runtime_Properties()
    {
        var publisher = CapturingPublisher(out var requests);
        var (orchestrator, _) = BuildOrchestrator(
            new DestinationsOptions
            {
                AzureBlob = [BlobDestination("despatchadvice-queue")],
                ServiceBus = [ServiceBusDestination(d => d.ApplicationProperties = new Dictionary<string, string> { ["senderId"] = "7350061190004" })]
            },
            publisher);

        await orchestrator.ProcessAsync(Event(), CancellationToken.None);

        var props = Assert.Single(requests).ApplicationProperties!;
        Assert.Equal("true", props["claimCheck"]);
        Assert.Equal("file-1", props["fh.fileId"]);
        Assert.Equal("local", props["fh.protocol"]);
        // Destination-configured properties survive: topic subscriptions may filter on them.
        Assert.Equal("7350061190004", props["senderId"]);
    }

    [Fact]
    public async Task ClaimCheck_Message_Reports_Blob_ContentType_Not_Json()
    {
        var publisher = CapturingPublisher(out var requests);
        var (orchestrator, _) = BuildOrchestrator(
            new DestinationsOptions
            {
                AzureBlob = [BlobDestination("despatchadvice-queue")],
                ServiceBus = [ServiceBusDestination()]
            },
            publisher,
            new FileWriteReceipt(12, new Uri(BlobUri), "application/edifact"));

        await orchestrator.ProcessAsync(Event(), CancellationToken.None);

        Assert.Equal("application/edifact", Assert.Single(requests).ContentType);
    }

    [Fact]
    public async Task ClaimCheck_Routes_To_Configured_ServiceBus_Destination_Not_The_Blob_One()
    {
        var publisher = CapturingPublisher(out var requests);
        var (orchestrator, _) = BuildOrchestrator(
            new DestinationsOptions
            {
                AzureBlob = [BlobDestination("despatchadvice-queue")],
                ServiceBus =
                [
                    ServiceBusDestination(d => { d.Name = "other-queue"; d.EntityName = "other"; }),
                    ServiceBusDestination(d => d.IsTopic = true)
                ]
            },
            publisher);

        await orchestrator.ProcessAsync(Event(), CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal("despatchadvice-queue", request.DestinationName);
        Assert.True(request.IsTopic);
    }

    [Fact]
    public async Task No_ClaimCheck_Configured_Publishes_Nothing()
    {
        var publisher = CapturingPublisher(out var requests);
        var (orchestrator, sink) = BuildOrchestrator(
            new DestinationsOptions
            {
                AzureBlob = [BlobDestination(null)],
                ServiceBus = [ServiceBusDestination()]
            },
            publisher);

        var result = await orchestrator.ProcessAsync(Event(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, sink.WriteCount);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task ClaimCheck_Publish_Failure_Fails_The_Transfer()
    {
        // The blob is already written, but nothing downstream knows about it. Failing here keeps the file
        // unmarked so it is retried rather than silently stranded.
        var publisher = CapturingPublisher(out _, Result.Failure(Error.Messaging.PublishTransient("boom")));
        var (orchestrator, _) = BuildOrchestrator(
            new DestinationsOptions
            {
                AzureBlob = [BlobDestination("despatchadvice-queue")],
                ServiceBus = [ServiceBusDestination()]
            },
            publisher);

        var result = await orchestrator.ProcessAsync(Event(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Messaging.PublishTransient", result.Error.Code);
    }

    [Fact]
    public async Task ClaimCheck_Fails_When_Sink_Reports_No_Blob_Location()
    {
        var publisher = CapturingPublisher(out var requests);
        var (orchestrator, _) = BuildOrchestrator(
            new DestinationsOptions
            {
                AzureBlob = [BlobDestination("despatchadvice-queue")],
                ServiceBus = [ServiceBusDestination()]
            },
            publisher,
            new FileWriteReceipt(5));

        var result = await orchestrator.ProcessAsync(Event(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(requests);
    }

    [Fact]
    public void Envelope_Serializes_To_The_Documented_Shape()
    {
        var json = System.Text.Encoding.UTF8.GetString(
            new ClaimCheckEnvelope("https://a/b/c.edi", "application/edifact", 4096).ToUtf8Json());

        Assert.Equal("{\"blobUrl\":\"https://a/b/c.edi\",\"contentType\":\"application/edifact\",\"length\":4096}", json);
    }
}
