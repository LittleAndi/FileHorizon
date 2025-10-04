# chore: secret resolver provider model

Labels: tech-debt, security

## Summary

Refactor secret resolution to a provider model enabling pluggable backends (e.g., Azure Key Vault) without modifying consuming code.

## Motivation

Current direct secret resolution approach (likely environment variables / in-memory) limits extension. Need consistent interface with multiple provider chain or prioritized resolution.

## Acceptance Criteria

- Introduce `ISecretProvider` interface returning `ValueTask<string?> GetSecretAsync(string name, CancellationToken)`.
- Implement default providers: EnvironmentVariableProvider, InMemoryProvider (for tests), CompositeProvider.
- `ISecretResolver` becomes facade orchestrating provider chain search order.
- Configuration to declare provider ordering.
- Tests: provider ordering, missing secret returns null, cancellation honored.
- Documentation update on adding new provider.

## Implementation Sketch

1. Add abstractions under `Abstractions` or `Common` (no infrastructure coupling).
2. Move existing logic into default provider(s).
3. DI registration extension updating existing usages transparently.
4. Add minimal logging (debug) when providers skip / miss (avoid secret value logging).

## Security

- Never log secret values. Only names/metadata in debug level.

## Open Questions

- Need caching layer w/ TTL? (Maybe separate future issue.)
