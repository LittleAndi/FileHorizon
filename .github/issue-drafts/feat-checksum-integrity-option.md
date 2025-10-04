# feat: checksum integrity option

Labels: enhancement, integrity

## Summary

Optional end-to-end checksum validation: compute hash (e.g., SHA256) when reading source and verify after sink write to ensure data integrity.

## Motivation

Detect silent corruption or partial writes across network boundaries or storage layers.

## Acceptance Criteria

- Config flag: `Integrity.Enabled` + algorithm selection (`SHA256`, maybe extendable later).
- Reader computes checksum while streaming (avoid double read) and passes along with content stream.
- Sink wrapper recomputes / verifies after write; mismatch triggers failure and delete of partial artifact if possible.
- Metrics: `integrity.verifications`, `integrity.failures`.
- Unit tests with simulated mismatch and success.

## Implementation Sketch

1. Extend file read pipeline to produce `(Stream stream, string checksum)` tuple or context struct.
2. Provide hashing utility (incremental) in `Common`.
3. Modify sinks or orchestrator to perform post-write verify when enabled.
4. Telemetry increments.

## Security

- Avoid logging raw checksum pairings with sensitive path; okay to log truncated first 8 chars for correlation.

## Idempotency

- Checksum could feed into enhanced idempotency strategy later (future coupling).

## Open Questions

- Support multi-part / chunked writes? (Future)
