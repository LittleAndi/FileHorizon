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

    /// <summary>Fully qualified Service Bus namespace (e.g., my-namespace.servicebus.windows.net) used when ConnectionString is not provided (Managed Identity path).</summary>
    public string? FullyQualifiedNamespace { get; init; }

    /// <summary>Optional managed identity client id (user-assigned) if a specific identity should be used for auth.</summary>
    public string? ManagedIdentityClientId { get; init; }
}
