namespace FileHorizon.Application.Configuration;

/// <summary>
/// Options controlling Service Bus notification publishing for processed files.
/// Phase 1: only Enabled flag to allow wiring later without deployment impact.
/// Future phases add connection + entity configuration.
/// </summary>
public sealed class ServiceBusNotificationOptions
{
    public bool Enabled { get; set; }
    public string? EntityName { get; set; } // queue or topic name
    public bool IsTopic { get; set; }
    public ServiceBusAuthMode AuthMode { get; set; } = ServiceBusAuthMode.ConnectionString;
    public string? ConnectionSecretRef { get; set; } // connection string when using ConnectionString mode
    public string? FullyQualifiedNamespace { get; set; } // e.g. mynamespace.servicebus.windows.net (AAD / SAS modes)
    public string? SasKeyNameRef { get; set; } // secret ref containing key name (SAS mode)
    public string? SasKeyValueRef { get; set; } // secret ref containing key value (SAS mode)
    public int MaxRetryAttempts { get; set; } = 5; // publish retry attempts
    public int BaseRetryDelayMs { get; set; } = 500; // initial backoff
    public int MaxRetryDelayMs { get; set; } = 10_000; // cap
    public bool EnableJitter { get; set; } = true;
    public int PublishTimeoutSeconds { get; set; } = 30; // per publish op timeout
    public bool LogFullPaths { get; set; } = true; // optional path redaction future
    public int IdempotencyTtlMinutes { get; set; } = 10; // suppression TTL
}

public enum ServiceBusAuthMode
{
    ConnectionString,
    AadManagedIdentity,
    SasKeyRef
}