# feat: enhanced idempotency key

Labels: enhancement, idempotency

## Summary

Derive a more collision-resistant composite idempotency key incorporating multiple file attributes and routing fingerprint.

## Motivation

Current key (assumed maybe path-based) risks collisions when files replaced in-place or across protocols. Need stronger uniqueness to prevent duplicate processing or missed reprocess events.

## Acceptance Criteria

- New key derivation: `hash(protocol + normalized_full_path + size + last_modified_utc + routing_fingerprint)`.
- Deterministic normalization for path (case, slash standardization).
- Routing fingerprint includes ordered destination identifiers + policy relevant options.
- Backwards-compatible migration path: continue accepting old keys until backlog drained; store both? (config flag `UseEnhancedIdempotency` default false initially).
- Add unit tests for stable hash output, path normalization edge cases, and changed attribute invalidating prior key.
- Document migration steps in README / docs.

## Implementation Sketch

1. Introduce `IdempotencyKeyStrategy` enum + service selecting algorithm.
2. Implement new strategy computing SHA256 hex (or base64url) of concatenated canonical string.
3. Extend `IIdempotencyStore` usage points to request strategy selection.
4. Add feature flag / configuration toggle.
5. Telemetry: metric `idempotency.strategy.usage` by strategy.

## Security

- Avoid logging raw composite string to protect potentially sensitive paths.

## Open Questions

- Should size or mtime be optional (some remote listings omit one)? Fallback plan needed.
