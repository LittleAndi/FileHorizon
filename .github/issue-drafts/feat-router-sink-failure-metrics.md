# feat: router & sink failure metrics

Labels: telemetry, observability

## Summary

Add granular metrics for routing matches, fan-out counts, and sink write failures to enhance operational visibility.

## Motivation

Operators need to quickly gauge routing effectiveness and identify problematic sinks/destinations. Current metrics may be insufficient to diagnose partial failures.

## Acceptance Criteria

- Metric: `router.matches` counter per file event (labels: `destination_count`).
- Metric: `router.fanout.count` (see fan-out issue) integrated; ensure not duplicated.
- Metric: `sink.write.failures` counter labeled by `destination_type` and `failure_reason` (coarse grouping).
- Metric: `sink.write.success` counter labeled by `destination_type`.
- Documentation update in `metrics-and-telemetry-dashboard.md` with new panels.
- Tests asserting metric emission (using test telemetry harness).

## Implementation Sketch

1. Extend `IFileProcessingTelemetry` with new methods or generic counter increment.
2. Implement in telemetry infrastructure adapter (Prometheus exporter likely already present).
3. Update orchestrator and sink invocation sites to emit metrics.
4. Dashboard JSON patch adding panels for success vs failure rates.

## Open Questions

- Should we include latency histograms per destination? (Maybe later; risk cardinality growth.)
