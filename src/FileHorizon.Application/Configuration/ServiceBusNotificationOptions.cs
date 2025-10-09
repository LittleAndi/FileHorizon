namespace FileHorizon.Application.Configuration;

/// <summary>
/// Options controlling Service Bus notification publishing for processed files.
/// Phase 1: only Enabled flag to allow wiring later without deployment impact.
/// Future phases add connection + entity configuration.
/// </summary>
public sealed class ServiceBusNotificationOptions
{
    public bool Enabled { get; set; }
    // Placeholder for future properties (EntityName, ConnectionSecretRef, Mode, etc.)
}