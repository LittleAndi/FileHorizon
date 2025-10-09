using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

public sealed class ServiceBusNotificationOptionsValidator : IValidateOptions<ServiceBusNotificationOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceBusNotificationOptions options)
    {
        if (options is null) return ValidateOptionsResult.Fail("Options instance is null");
        if (!options.Enabled) return ValidateOptionsResult.Success; // disabled => minimal validation

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.EntityName)) failures.Add("EntityName is required when Enabled=true");

        switch (options.AuthMode)
        {
            case ServiceBusAuthMode.ConnectionString:
                if (string.IsNullOrWhiteSpace(options.ConnectionSecretRef)) failures.Add("ConnectionSecretRef required for ConnectionString mode");
                if (!string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace)) failures.Add("FullyQualifiedNamespace must be null for ConnectionString mode");
                break;
            case ServiceBusAuthMode.AadManagedIdentity:
                if (string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace)) failures.Add("FullyQualifiedNamespace required for AadManagedIdentity mode");
                if (!string.IsNullOrWhiteSpace(options.ConnectionSecretRef)) failures.Add("ConnectionSecretRef must be null for AadManagedIdentity mode");
                if (!string.IsNullOrWhiteSpace(options.SasKeyNameRef) || !string.IsNullOrWhiteSpace(options.SasKeyValueRef)) failures.Add("SAS key refs must be null for AadManagedIdentity mode");
                break;
            case ServiceBusAuthMode.SasKeyRef:
                if (string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace)) failures.Add("FullyQualifiedNamespace required for SasKeyRef mode");
                if (string.IsNullOrWhiteSpace(options.SasKeyNameRef)) failures.Add("SasKeyNameRef required for SasKeyRef mode");
                if (string.IsNullOrWhiteSpace(options.SasKeyValueRef)) failures.Add("SasKeyValueRef required for SasKeyRef mode");
                if (!string.IsNullOrWhiteSpace(options.ConnectionSecretRef)) failures.Add("ConnectionSecretRef must be null for SasKeyRef mode");
                break;
            default:
                failures.Add($"Unknown AuthMode '{options.AuthMode}'");
                break;
        }

        if (options.MaxRetryAttempts <= 0) failures.Add("MaxRetryAttempts must be > 0");
        if (options.BaseRetryDelayMs <= 0) failures.Add("BaseRetryDelayMs must be > 0");
        if (options.MaxRetryDelayMs < options.BaseRetryDelayMs) failures.Add("MaxRetryDelayMs must be >= BaseRetryDelayMs");
        if (options.PublishTimeoutSeconds <= 0) failures.Add("PublishTimeoutSeconds must be > 0");
        if (options.IdempotencyTtlMinutes <= 0) failures.Add("IdempotencyTtlMinutes must be > 0");
        if (options.CircuitBreakerEnabled)
        {
            if (options.CircuitBreakerFailureThreshold <= 0) failures.Add("CircuitBreakerFailureThreshold must be > 0 when enabled");
            if (options.CircuitBreakerResetSeconds <= 0) failures.Add("CircuitBreakerResetSeconds must be > 0 when enabled");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}