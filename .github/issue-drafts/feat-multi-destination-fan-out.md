# feat: implement multi-destination fan-out

Labels: enhancement, processing

## Summary

Support routing a single validated file event to multiple destination plans (fan-out). Currently only the first/primary destination is actioned. Add a loop over all routed `DestinationPlan` entries with a configurable failure policy.

## Motivation / Problem

Some ingestion workflows require writing the same source file to multiple sinks (e.g., analytics bucket + archival store + downstream SFTP). Lack of fan-out forces extra orchestration outside FileHorizon.

## Goals / Acceptance Criteria

- Given a file event with routing that resolves N>1 destination plans, the system attempts all.
- New option: `RoutingOptions.FanOutFailurePolicy` with values `StrictAllMustSucceed` (default) or `PartialOk`.
- In `StrictAllMustSucceed`: any single sink failure marks overall processing failure and triggers existing retry / dead-letter behavior.
- In `PartialOk`: successes are recorded individually; failures produce a warning metric + structured log; overall result = success if >=1 succeeded.
- Per-destination results captured for telemetry (success/failure + latency + bytes written).
- Metrics: `router.fanout.count` (total destinations acted upon) and `router.fanout.failures` (count of failed destination writes) with labels: `destination_type`, `policy`.
- Existing single-destination flows remain unaffected when only one plan present.

## Non-Goals

- Transactional all-or-nothing multi-sink commit semantics.
- Cross-destination consistency validation.

## Proposed Implementation Sketch

1. Extend routing result model to expose `IReadOnlyList<DestinationPlan>` if not already.
2. Introduce failure policy enum in `Models` or `Configuration`.
3. Update orchestrator / processor to iterate destinations, collecting `Result` objects.
4. Aggregate result per policy branch.
5. Emit new metrics via `IFileProcessingTelemetry`.
6. Add tests: single-destination unchanged, multi with all success, multi with one failure under both policies.

## Telemetry / Metrics

- `router.fanout.count` (counter) labels: `policy`.
- `router.fanout.failures` (counter) labels: `destination_type`, `policy`.
- Potential span attribute: `fanout.count`.

## Testing Strategy

- Unit: orchestrator fan-out logic.
- Integration: multi-sink scenario with mixed success using in-memory sinks.
- Property-style test (optional): order of destinations does not alter aggregate result.

## Security / Idempotency Considerations

- Ensure idempotency key derivation (see enhanced idempotency issue) can incorporate all destinations or remain stable; for now maintain current key (per file) so repeated processing still safe.

## Open Questions

- Need back-pressure if large fan-out counts? (Maybe later)
- Should partial failures trigger per-destination retry queue separate from main? (Out of scope now)
