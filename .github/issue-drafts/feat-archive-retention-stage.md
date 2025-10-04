# feat: archive / retention stage

Labels: enhancement, processing

## Summary

Optional post-success archival stage that copies the processed file into an archive location with date partitioning before (or instead of) deletion.

## Motivation

Compliance and audit requirements sometimes mandate retention of original source artifacts for a period. Current pipeline deletes after success losing traceability unless external archival exists.

## Acceptance Criteria

- Config flag `Archive.Enabled` (default false).
- Supports `Archive.Mode` = `CopyThenDelete` or `Move` (if same storage backend type) + `RetentionDays` (for future purge logic).
- Date-partitioned path template: `{yyyy}/{MM}/{dd}/` prefix plus original relative path.
- Metrics: `archive.files.copied`, `archive.bytes.copied`, `archive.failures`.
- If archival fails under `CopyThenDelete`, deletion is skipped and failure logged (configurable policy?).
- Tests for path templating, success, and failure fallback.

## Implementation Sketch

1. Introduce `IArchiveService` abstraction.
2. Implement local filesystem archival first (infrastructure) with streaming copy.
3. Integrate orchestrator post-success pre-delete step.
4. Extend telemetry.
5. Add retention purge placeholder (separate future issue).

## Security / Idempotency

- Idempotent copy: if target exists with same size, skip or verify hash (optional future checksum integration).

## Open Questions

- Need configurable naming collision policy (overwrite vs version)?
