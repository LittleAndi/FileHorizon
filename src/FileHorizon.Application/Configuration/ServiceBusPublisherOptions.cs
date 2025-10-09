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
}
