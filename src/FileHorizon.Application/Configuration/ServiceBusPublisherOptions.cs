namespace FileHorizon.Application.Configuration;

/// <summary>
/// Options governing Azure Service Bus file content publishing.
/// </summary>
public sealed class ServiceBusPublisherOptions
{
    /// <summary>Connection string for Azure Service Bus or empty when using managed identity (future).</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Maximum concurrent publish operations.</summary>
    public int MaxConcurrentPublishes { get; init; } = 4;


    /// <summary>Enable diagnostic tracing integration.</summary>
    public bool EnableTracing { get; init; } = true;

    /// <summary>Number of retry attempts for transient publish failures (not counting the initial attempt). Set to 0 to disable retries.</summary>
    public int PublishRetryCount { get; init; } = 3;

    /// <summary>Base delay in milliseconds for the first retry attempt.</summary>
    public int PublishRetryBaseDelayMs { get; init; } = 200;

    /// <summary>Maximum delay cap in milliseconds for exponential backoff.</summary>
    public int PublishRetryMaxDelayMs { get; init; } = 4000;
}
