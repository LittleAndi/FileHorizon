using FileHorizon.Application.Abstractions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace FileHorizon.Application.Infrastructure.Secrets;

/// <summary>
/// Development placeholder secret resolver. Looks up values from environment variables first (by exact key),
/// then an internal registration dictionary. Intended to be replaced by a Key Vault implementation in Host layer.
/// </summary>
public sealed class InMemorySecretResolver(ILogger<InMemorySecretResolver> logger) : ISecretResolver
{
    private readonly ILogger<InMemorySecretResolver> _logger = logger;
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> ResolveSecretAsync(string? secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef)) return Task.FromResult<string?>(null);

        // Environment variable first
        var env = Environment.GetEnvironmentVariable(secretRef);
        if (!string.IsNullOrEmpty(env)) return Task.FromResult<string?>(env);

        if (_secrets.TryGetValue(secretRef, out var value)) return Task.FromResult<string?>(value);

        _logger.LogDebug("Secret reference {Ref} not found in environment or in-memory store", secretRef);
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// For local dev/testing code paths to preload a secret programmatically (not used in production).
    /// </summary>
    public void Set(string key, string value) => _secrets[key] = value;
}
