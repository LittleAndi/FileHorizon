using Azure;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Storage;
using FileHorizon.Application.Models;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public class AzureBlobStorageClientTests
{
    private static readonly BlobUploadRequest Request = new(
        DestinationName: "archive",
        ContainerName: "incoming",
        BlobPath: "sub/file.txt",
        ContentType: "text/plain",
        Overwrite: false,
        AccessTier: null);

    [Theory]
    [InlineData(409, "BlobAlreadyExists", "Storage.AlreadyExists")]
    [InlineData(409, "ConditionNotMet", "Storage.AlreadyExists")]
    [InlineData(404, "ContainerNotFound", "Storage.ContainerMissing")]
    [InlineData(401, null, "Storage.Authorization")]
    [InlineData(403, "AuthorizationPermissionMismatch", "Storage.Authorization")]
    [InlineData(429, null, "Storage.UploadTransient")]
    [InlineData(500, "InternalError", "Storage.UploadTransient")]
    [InlineData(503, "ServerBusy", "Storage.UploadTransient")]
    [InlineData(400, "InvalidHeaderValue", "Storage.UploadError")]
    public void MapError_Translates_RequestFailures(int status, string? errorCode, string expectedCode)
    {
        var ex = new RequestFailedException(status, "simulated failure", errorCode, null);
        var error = AzureBlobStorageClient.MapError(ex, Request);
        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void MapError_Message_Is_SingleLine()
    {
        var ex = new RequestFailedException(500, "first line\r\nHeaders:\r\nAuthorization: secret", null, null);
        var error = AzureBlobStorageClient.MapError(ex, Request);
        Assert.Equal("first line", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task UploadAsync_Fails_When_Destination_NotConfigured()
    {
        var client = new AzureBlobStorageClient(
            NullLogger<AzureBlobStorageClient>.Instance,
            new OptionsMonitorStub<DestinationsOptions>(new DestinationsOptions()));
        var res = await client.UploadAsync(Request, new MemoryStream([1, 2, 3]), CancellationToken.None);
        Assert.True(res.IsFailure);
        Assert.Equal("Storage.NotConfigured", res.Error.Code);
    }

    [Fact]
    public async Task UploadAsync_Fails_When_No_Auth_Configured()
    {
        var destinations = new DestinationsOptions
        {
            AzureBlob =
            [
                new AzureBlobDestinationOptions
                {
                    Name = "archive",
                    ContainerName = "incoming",
                    BlobTechnical = new AzureBlobTechnicalOptions() // neither connection string, account nor service uri
                }
            ]
        };
        var client = new AzureBlobStorageClient(
            NullLogger<AzureBlobStorageClient>.Instance,
            new OptionsMonitorStub<DestinationsOptions>(destinations));
        var res = await client.UploadAsync(Request, new MemoryStream([1, 2, 3]), CancellationToken.None);
        Assert.True(res.IsFailure);
        Assert.Equal("Storage.NotConfigured", res.Error.Code);
    }
}
