using System.Text;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Models;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileHorizon.Application.Tests;

public class AzureBlobFileSinkTests
{
    private sealed class FakeBlobStorageClient : IBlobStorageClient
    {
        public BlobUploadRequest? LastRequest { get; private set; }
        public Result<BlobUploadResult>? NextResult { get; set; }

        public Task<Result<BlobUploadResult>> UploadAsync(BlobUploadRequest request, Stream content, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(NextResult ?? Result<BlobUploadResult>.Success(new BlobUploadResult(
                AccountName: "testaccount",
                ContainerName: request.ContainerName,
                BlobPath: request.BlobPath,
                BlobUri: new Uri($"https://testaccount.blob.core.windows.net/{request.ContainerName}/{request.BlobPath}"),
                BytesWritten: content.CanSeek ? content.Length : 0)));
        }
    }

    private static AzureBlobDestinationOptions Destination(Action<AzureBlobDestinationOptions>? configure = null)
    {
        var d = new AzureBlobDestinationOptions
        {
            Name = "archive",
            ContainerName = "incoming",
            BlobTechnical = new AzureBlobTechnicalOptions { AccountName = "testaccount" }
        };
        configure?.Invoke(d);
        return d;
    }

    private static (AzureBlobFileSink Sink, FakeBlobStorageClient Client) CreateSink(
        AzureBlobDestinationOptions destination, IFileTypeDetector? detector = null)
    {
        var client = new FakeBlobStorageClient();
        var monitor = new OptionsMonitorStub<DestinationsOptions>(new DestinationsOptions { AzureBlob = [destination] });
        var sink = new AzureBlobFileSink(
            NullLogger<AzureBlobFileSink>.Instance,
            client,
            monitor,
            detector ?? new ExtensionFileTypeDetector());
        return (sink, client);
    }

    private static FileReference Target(string path, string? sourceName = "archive", string scheme = AzureBlobFileSink.Scheme)
        => new(scheme, null, null, path, sourceName);

    private static FileWriteOptions WriteOptions(bool overwrite = false) => new(overwrite, false);

    private static MemoryStream Content() => new(Encoding.UTF8.GetBytes("hello"));

    [Fact]
    public async Task WriteAsync_ComposesBlobPath_WithPrefix()
    {
        var (sink, client) = CreateSink(Destination(d => d.RootPathPrefix = "ingest/2025/"));
        var res = await sink.WriteAsync(Target(@"sub\file.txt"), Content(), WriteOptions(), CancellationToken.None);
        Assert.True(res.IsSuccess);
        Assert.Equal("ingest/2025/sub/file.txt", client.LastRequest!.BlobPath);
        Assert.Equal("incoming", client.LastRequest.ContainerName);
    }

    [Theory]
    [InlineData(null, "file.txt", "file.txt")]
    [InlineData("", "file.txt", "file.txt")]
    [InlineData("/prefix/", "/file.txt", "prefix/file.txt")]
    [InlineData("a/b", "c/d.bin", "a/b/c/d.bin")]
    public void ComposeBlobPath_Normalizes(string? prefix, string path, string expected)
    {
        Assert.Equal(expected, AzureBlobFileSink.ComposeBlobPath(prefix, path));
    }

    [Fact]
    public async Task WriteAsync_Fails_ForUnknownDestination()
    {
        var (sink, client) = CreateSink(Destination());
        var res = await sink.WriteAsync(Target("file.txt", sourceName: "other"), Content(), WriteOptions(), CancellationToken.None);
        Assert.True(res.IsFailure);
        Assert.Equal("Storage.NotConfigured", res.Error.Code);
        Assert.Null(client.LastRequest);
    }

    [Fact]
    public async Task WriteAsync_Fails_ForNonBlobScheme()
    {
        var (sink, client) = CreateSink(Destination());
        var res = await sink.WriteAsync(Target("file.txt", scheme: "local"), Content(), WriteOptions(), CancellationToken.None);
        Assert.True(res.IsFailure);
        Assert.Equal("Validation.Invalid", res.Error.Code);
        Assert.Null(client.LastRequest);
    }

    [Fact]
    public async Task WriteAsync_OverwritePolicy_Overwrite_Wins_Over_RuleFlag()
    {
        var (sink, client) = CreateSink(Destination(d => d.OverwritePolicy = BlobOverwritePolicy.Overwrite));
        await sink.WriteAsync(Target("file.txt"), Content(), WriteOptions(overwrite: false), CancellationToken.None);
        Assert.True(client.LastRequest!.Overwrite);
    }

    [Fact]
    public async Task WriteAsync_OverwritePolicy_FailIfExists_Wins_Over_RuleFlag()
    {
        var (sink, client) = CreateSink(Destination(d => d.OverwritePolicy = BlobOverwritePolicy.FailIfExists));
        await sink.WriteAsync(Target("file.txt"), Content(), WriteOptions(overwrite: true), CancellationToken.None);
        Assert.False(client.LastRequest!.Overwrite);
    }

    [Fact]
    public async Task WriteAsync_OverwritePolicy_Unset_FollowsRuleFlag()
    {
        var (sink, client) = CreateSink(Destination());
        await sink.WriteAsync(Target("file.txt"), Content(), WriteOptions(overwrite: true), CancellationToken.None);
        Assert.True(client.LastRequest!.Overwrite);

        await sink.WriteAsync(Target("file.txt"), Content(), WriteOptions(overwrite: false), CancellationToken.None);
        Assert.False(client.LastRequest!.Overwrite);
    }

    [Fact]
    public async Task WriteAsync_ContentType_Provided_UsesConfiguredValue()
    {
        var (sink, client) = CreateSink(Destination(d =>
        {
            d.ContentTypeStrategy = BlobContentTypeStrategy.Provided;
            d.ContentType = "application/x-custom";
        }));
        await sink.WriteAsync(Target("file.bin"), Content(), WriteOptions(), CancellationToken.None);
        Assert.Equal("application/x-custom", client.LastRequest!.ContentType);
    }

    [Fact]
    public async Task WriteAsync_ContentType_None_SendsNull()
    {
        var (sink, client) = CreateSink(Destination(d => d.ContentTypeStrategy = BlobContentTypeStrategy.None));
        await sink.WriteAsync(Target("file.txt"), Content(), WriteOptions(), CancellationToken.None);
        Assert.Null(client.LastRequest!.ContentType);
    }

    [Fact]
    public async Task WriteAsync_ContentType_Inferred_FromExtension()
    {
        var (sink, client) = CreateSink(Destination());
        await sink.WriteAsync(Target("file.txt"), Content(), WriteOptions(), CancellationToken.None);
        Assert.Equal("text/plain", client.LastRequest!.ContentType);
    }

    [Fact]
    public async Task WriteAsync_ContentType_FallsBack_To_OctetStream_WhenUnknown()
    {
        var (sink, client) = CreateSink(Destination());
        await sink.WriteAsync(Target("file.unknownext"), Content(), WriteOptions(), CancellationToken.None);
        Assert.Equal("application/octet-stream", client.LastRequest!.ContentType);
    }

    [Fact]
    public async Task WriteAsync_PassesThrough_AccessTier()
    {
        var (sink, client) = CreateSink(Destination(d => d.AccessTier = "Cool"));
        await sink.WriteAsync(Target("file.txt"), Content(), WriteOptions(), CancellationToken.None);
        Assert.Equal("Cool", client.LastRequest!.AccessTier);
    }

    [Fact]
    public async Task WriteAsync_Propagates_ClientFailure()
    {
        var (sink, client) = CreateSink(Destination());
        client.NextResult = Result<BlobUploadResult>.Failure(Error.Storage.AlreadyExists("incoming/file.txt"));
        var res = await sink.WriteAsync(Target("file.txt"), Content(), WriteOptions(), CancellationToken.None);
        Assert.True(res.IsFailure);
        Assert.Equal("Storage.AlreadyExists", res.Error.Code);
    }
}
