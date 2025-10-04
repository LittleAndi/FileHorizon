# feat: add per-destination retry policies

Labels: enhancement, reliability

## Summary

Introduce configurable retry/backoff policies per destination (sink) write operation instead of a single uniform policy.

## Motivation

Different sinks have different transient failure profiles (e.g., S3 vs SFTP vs local FS). A one-size-fits-all retry wastes time or causes premature failure.

## Acceptance Criteria

- New configuration section (e.g., `TransferOptions:Destinations[]:RetryPolicy`) specifying: `MaxAttempts`, `BackoffStrategy (Fixed|Exponential)`, `BaseDelay`, `MaxDelay`, `Jitter (bool)`.
- Default global fallback if destination-specific policy absent.
- Processor applies retry policy individually per destination plan.
- Metrics: `sink.write.retry.attempts` (counter) labelled by `destination_type` and `success` (bool).
- On exhausted retries, existing failure pathways engaged unchanged.
- Unit tests cover: fixed backoff, exponential with cap, jitter inclusion (approx assertion), and fallback to global policy.

## Implementation Sketch

1. Add retry policy model in `Models` or `Configuration` (immutable record).
2. Extend routing or plan structure to surface the chosen policy.
3. Implement retry helper (pure, deterministic) returning next delay sequence.
4. Integrate into the sink write loop; respect cancellation token.
5. Emit attempt metrics and final result status.

## Telemetry / Metrics

- `sink.write.retry.attempts` counter.
- Optional histogram: `sink.write.retry.delay.ms` (future).

## Testing Strategy

- Unit tests for delay sequence generation.
- Orchestrator tests with simulated transient failures (mock sink).

## Security / Idempotency

- Ensure retries do not change idempotency key; sink writes must be idempotent internally (e.g., overwrite safe or conditional logic).

## Open Questions

- Should we add circuit breaker semantics? (Future enhancement)
