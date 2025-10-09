using System.Text;
using FileHorizon.Application.Infrastructure.Messaging.ServiceBus;
using FileHorizon.Application.Models;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FileHorizon.Application.Common;

namespace FileHorizon.Application.Tests;

public class ServiceBusFileContentPublisherTests
{
    private AzureServiceBusFileContentPublisher CreatePublisher(string connection = "Endpoint=sb://localhost/;SharedAccessKeyName=Root;SharedAccessKey=Fake=")
    {
        var options = Options.Create(new ServiceBusPublisherOptions { ConnectionString = connection });
        return new AzureServiceBusFileContentPublisher(options, NullLogger<AzureServiceBusFileContentPublisher>.Instance);
    }

    [Fact(Skip = "Requires live Service Bus or refactoring for mock client")]
    public async Task WholeFileMode_Publishes_Success()
    {
        var publisher = CreatePublisher();
        var content = Encoding.UTF8.GetBytes("hello world");
        var req = new FilePublishRequest("/tmp", "test.txt", content, "text/plain", "queue1", false);
        var result = await publisher.PublishAsync(req, CancellationToken.None);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Validation_Fails_On_Empty_Destination()
    {
        var publisher = CreatePublisher();
        var content = Encoding.UTF8.GetBytes("data");
        var req = new FilePublishRequest("/tmp", "a.txt", content, "text/plain", "", false);
        var result = await publisher.PublishAsync(req, CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal("Messaging.DestinationEmpty", result.Error.Code);
    }

    [Fact]
    public async Task Validation_Fails_On_Empty_Content()
    {
        var publisher = CreatePublisher();
        var empty = Array.Empty<byte>();
        var req = new FilePublishRequest("/tmp", "a.txt", empty, "text/plain", "queue1", false);
        var result = await publisher.PublishAsync(req, CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal("Messaging.ContentEmpty", result.Error.Code);
    }
}
