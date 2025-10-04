# feat: service bus egress publisher

Labels: enhancement, integration

## Summary

Publish file processed notifications (success/failure + metadata) to a configured Azure Service Bus queue or topic with idempotent suppression to avoid duplicates.

## Motivation

Downstream consumers may need near-real-time events of processed files for chaining workflows (indexing, ETL). Central publisher reduces custom code duplication.

## Acceptance Criteria

- Configuration: target entity (queue/topic), credential/connection secret ref, publish mode (success|failure|both).
- Message schema includes file identity, size, mtime, processing duration, destination summary, result status, correlation/idempotency key.
- Idempotent publish: track last published event IDs to avoid repeat on retries.
- Metrics: `servicebus.egress.publishes`, `servicebus.egress.failures`.
- Backpressure handling: exponential retry with max attempts; failures escalate to log + metric only (no crash).

## Implementation Sketch

1. Define notification record -> JSON serialization.
2. Add `IFileProcessedNotifier` abstraction if not present; implement Service Bus publisher.
3. Integrate in orchestrator after final result resolution.
4. Idempotency store extension or in-memory cache for duplicate suppression.
5. Telemetry instrumentation.

## Security

- Secret resolution via `ISecretResolver`.

## Open Questions

- Batch publish optimization? (Future)
