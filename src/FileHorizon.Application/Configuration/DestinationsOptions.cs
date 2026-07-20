namespace FileHorizon.Application.Configuration;

public sealed class DestinationsOptions
{
    public const string SectionName = "Destinations";

    public List<LocalDestinationOptions> Local { get; set; } = [];
    public List<SftpDestinationOptions> Sftp { get; set; } = [];
    public List<ServiceBusDestinationOptions> ServiceBus { get; set; } = [];
    public List<AzureBlobDestinationOptions> AzureBlob { get; set; } = [];
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
    public Dictionary<string, string>? ApplicationProperties { get; set; } // optional custom application properties to add to every message sent to this destination
    
    /// <summary>
    /// Enable gzip compression for message bodies sent to this destination. Default is false.
    /// When enabled, messages are compressed before sending and a 'Content-Encoding: gzip' header is added to application properties.
    /// </summary>
    /// <remarks>
    /// Compression is beneficial for:
    /// - Large text files (XML, JSON, CSV, log files)
    /// - Repetitive data that compresses well
    /// - Reducing bandwidth usage and Service Bus message size limits
    /// 
    /// Note: Small messages (< 1KB) may become larger due to gzip overhead.
    /// Receivers must check the 'Content-Encoding' application property and decompress accordingly.
    /// 
    /// Example configuration:
    /// <code>
    /// "Destinations": {
    ///   "ServiceBus": [{
    ///     "Name": "LargeFilesQueue",
    ///     "EntityName": "large-files",
    ///     "EnableGzipCompression": true,
    ///     "ServiceBusTechnical": {
    ///       "ConnectionString": "Endpoint=sb://..."
    ///     }
    ///   }]
    /// }
    /// </code>
    /// 
    /// Environment variable:
    /// Destinations__ServiceBus__0__EnableGzipCompression=true
    /// </remarks>
    public bool EnableGzipCompression { get; set; } = false;
    
    public ServiceBusTechnicalOptions ServiceBusTechnical { get; set; } = new(); // destination specific Service Bus settings (retries, tracing, MI namespace)
}

public sealed class AzureBlobDestinationOptions
{
    public string Name { get; set; } = string.Empty; // logical destination name used in routing rules
    public string ContainerName { get; set; } = string.Empty;
    /// <summary>Optional virtual folder prefix prepended to every blob path (e.g. "ingest/2025").</summary>
    public string? RootPathPrefix { get; set; }
    /// <summary>Optional access tier applied on upload: Hot | Cool | Cold | Archive.</summary>
    public string? AccessTier { get; set; }
    /// <summary>How the blob Content-Type is determined. Default infers from the file extension.</summary>
    public BlobContentTypeStrategy ContentTypeStrategy { get; set; } = BlobContentTypeStrategy.InferFromExtension;
    /// <summary>Content type applied when ContentTypeStrategy is Provided.</summary>
    public string? ContentType { get; set; }
    /// <summary>Overwrite behavior for existing blobs. When omitted, the routing rule's Overwrite flag applies.</summary>
    public BlobOverwritePolicy? OverwritePolicy { get; set; }
    public AzureBlobTechnicalOptions BlobTechnical { get; set; } = new(); // destination specific storage settings (auth, retries)
}

public enum BlobContentTypeStrategy
{
    InferFromExtension = 0,
    Provided = 1,
    None = 2
}

public enum BlobOverwritePolicy
{
    FailIfExists = 0,
    Overwrite = 1
}

public sealed class AzureBlobTechnicalOptions
{
    /// <summary>Connection string enabling the destination; if null/empty falls back to managed identity (AccountName or ServiceUri).</summary>
    public string? ConnectionString { get; set; }
    /// <summary>Storage account name used to build https://{AccountName}.blob.core.windows.net for managed identity auth.</summary>
    public string? AccountName { get; set; }
    /// <summary>Optional full blob service URI (takes precedence over AccountName; useful for Azurite or sovereign clouds).</summary>
    public string? ServiceUri { get; set; }
    /// <summary>Optional managed identity client id (user-assigned) if a specific identity should be used for auth.</summary>
    public string? ManagedIdentityClientId { get; init; }
    /// <summary>Maximum retry attempts for transient upload failures (Azure SDK retry policy). Set to 0 to disable retries.</summary>
    public int MaxRetries { get; init; } = 3;
    /// <summary>Base delay in milliseconds for the first retry attempt.</summary>
    public int RetryBaseDelayMs { get; init; } = 500;
    /// <summary>Maximum delay cap in milliseconds for exponential backoff.</summary>
    public int RetryMaxDelayMs { get; init; } = 8000;
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
