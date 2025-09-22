namespace FileHorizon.Application.Abstractions;

/// <summary>
/// Abstraction for resolving secret references (e.g., Key Vault, environment, external secret store).
/// Implementations should cache appropriately and avoid logging secret values.
/// </summary>
public interface ISecretResolver
{
    /// <summary>
    /// Resolve a secret reference to its plain text value.
    /// Returns null if the reference is null/empty or cannot be resolved.
    /// </summary>
    Task<string?> ResolveSecretAsync(string? secretRef, CancellationToken cancellationToken = default);
}
