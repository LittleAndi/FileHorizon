namespace FileHorizon.Application.Configuration;

public sealed class DestinationsOptions
{
    public const string SectionName = "Destinations";

    public List<LocalDestinationOptions> Local { get; set; } = [];
    public List<SftpDestinationOptions> Sftp { get; set; } = [];
    public List<ServiceBusDestinationOptions> ServiceBus { get; set; } = [];
}

public sealed class LocalDestinationOptions
{
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
}

public sealed class SftpDestinationOptions
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string? PasswordSecretRef { get; set; }
    public string? PrivateKeySecretRef { get; set; }
    public string? PrivateKeyPassphraseSecretRef { get; set; }
    public string RootPath { get; set; } = "/";
    public bool StrictHostKey { get; set; } = false;
}

public sealed class ServiceBusDestinationOptions
{
    public string Name { get; set; } = string.Empty; // logical destination name used in routing rules
    public string EntityName { get; set; } = string.Empty; // queue or topic name
    public bool IsTopic { get; set; } = false; // true if EntityName refers to a topic
    public string? ContentType { get; set; } // optional override for message content type, defaults to text/plain
    public ServiceBusTechnicalOptions ServiceBusTechnical { get; set; } = new(); // destination specific Service Bus settings (retries, tracing, MI namespace)
}

public sealed class ServiceBusTechnicalOptions
{
    /// <summary>Connection string enabling publisher; if null/empty falls back to managed identity (namespace on ServiceBusPublisherOptions).</summary>
    public string? ConnectionString { get; set; } // connection string enabling publisher; if null/empty falls back to managed identity (namespace on ServiceBusPublisherOptions)
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
    /// <summary>Optional fully qualified Service Bus namespace (e.g., my-namespace.servicebus.windows.net) used when a destination does not specify a connection string (Managed Identity path).</summary>
    public string? FullyQualifiedNamespace { get; init; }
    /// <summary>Optional managed identity client id (user-assigned) if a specific identity should be used for auth.</summary>
    public string? ManagedIdentityClientId { get; init; }
}
