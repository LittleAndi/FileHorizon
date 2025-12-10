using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace FileHorizon.Application.Tests;

public class DestinationsOptionsValidatorTests
{
    private readonly DestinationsOptionsValidator _validator = new();

    [Fact]
    public void Validate_ServiceBusDestination_WithValidConfig_Succeeds()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "test-queue",
                    IsTopic = false,
                    ContentType = "application/json",
                    ApplicationProperties = new Dictionary<string, string>
                    {
                        ["correlationId"] = "12345",
                        ["customLabel"] = "test-label"
                    },
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=Test;SharedAccessKey=abc123="
                    }
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ServiceBusDestination_WithManagedIdentity_Succeeds()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "test-queue",
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        FullyQualifiedNamespace = "test-namespace.servicebus.windows.net"
                    }
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ServiceBusDestination_WithoutAuthenticationMethod_Fails()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "test-queue",
                    ServiceBusTechnical = new ServiceBusTechnicalOptions()
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("ConnectionString", result.FailureMessage);
        Assert.Contains("FullyQualifiedNamespace", result.FailureMessage);
        Assert.Contains("must be specified", result.FailureMessage);
    }

    [Fact]
    public void Validate_ServiceBusDestination_WithReservedKey_Fails()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "test-queue",
                    ApplicationProperties = new Dictionary<string, string>
                    {
                        ["fh.fileId"] = "override-attempt"
                    },
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;"
                    }
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("fh.fileId", result.FailureMessage);
        Assert.Contains("reserved", result.FailureMessage);
    }

    [Fact]
    public void Validate_ServiceBusDestination_WithContentEncodingKey_Fails()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "test-queue",
                    ApplicationProperties = new Dictionary<string, string>
                    {
                        ["Content-Encoding"] = "custom"
                    },
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;"
                    }
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("Content-Encoding", result.FailureMessage);
        Assert.Contains("reserved", result.FailureMessage);
    }

    [Fact]
    public void Validate_ServiceBusDestination_MissingName_Fails()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "",
                    EntityName = "test-queue",
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;"
                    }
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("Name must be specified", result.FailureMessage);
    }

    [Fact]
    public void Validate_ServiceBusDestination_MissingEntityName_Fails()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "",
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;"
                    }
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("EntityName must be specified", result.FailureMessage);
    }

    [Fact]
    public void Validate_ServiceBusDestination_DuplicateName_Fails()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "queue1",
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;"
                    }
                },
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "queue2",
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;"
                    }
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("duplicated", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ServiceBusDestination_NoApplicationProperties_Succeeds()
    {
        var options = new DestinationsOptions
        {
            ServiceBus =
            [
                new ServiceBusDestinationOptions
                {
                    Name = "TestQueue",
                    EntityName = "test-queue",
                    ApplicationProperties = null,
                    ServiceBusTechnical = new ServiceBusTechnicalOptions
                    {
                        ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;"
                    }
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }
}
