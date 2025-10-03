# Next Steps

## Observability (OpenTelemetry)

- Add OpenTelemetry packages (Tracing, Metrics, Logging) in Host only.
- Configure Resource builder with service.name=FileHorizon
- Exporters: Prometheus + OTLP (future). No other logging frameworks.

## Redis Streams Integration

- Add abstractions for Redis stream consumer/producer (already partial via IFileEventQueue).
- Implement Infrastructure/Redis/RedisFileEventQueue using StackExchange.Redis.
- Handle consumer group creation, claim pending messages on startup, ack after processing.

## Azure Service Bus Ingress / Egress (External Integration)

- Purpose: external systems publish commands (ingress) and receive processing notifications (egress) without coupling to Redis internals.
- Ingress Bridge:
  - Implement `ServiceBusIngressListener` that reads from a queue/topic and translates messages into internal `IFileEventQueue` entries (Redis Streams writes).
  - Validate and enrich incoming commands (e.g., resolve source protocol + path) before enqueue.
  - Dead-letter malformed or repeatedly failing ingress messages with diagnostic metadata.
- Egress Publisher:
  - Implement `ServiceBusNotificationPublisher` invoked after successful file processing (hook inside FileProcessingService or post-processing pipeline decorator).
  - Publish minimal metadata (file id, size, checksum, status, archive location) to a topic for downstream subscribers.
  - Retry transient failures; send to dead-letter (or fallback log) on permanent failure.
- Options Classes: `ServiceBusIngressOptions`, `ServiceBusEgressOptions` (entity names, enable flags, retry policies, publish filter rules).
- Security: prefer managed identity; fallback to connection string only for local dev.
- Idempotency: reuse internal processed file registry to prevent duplicate egress publishes (guard by file identity hash) if retries occur.
- Telemetry: add spans/metrics (ingress message count, ingress DLQ count, egress publish latency, egress failures).
- Feature Flags: `EnableServiceBusIngress`, `EnableServiceBusEgress` for incremental rollout.

## Pollers for Protocols (Implemented)

- Local (UNC), FTP, and SFTP pollers implemented behind a shared `IFilePoller` abstraction.
- Composite `MultiProtocolPoller` delegates sequentially; each poller independently feature flagged.
- Remote sources configured under `RemoteFileSources:Sources` with per-entry readiness (size stability) and secret indirection.
- Exponential backoff per source (base 5s, cap 5m) resets on success.
- De-duplication via normalized protocol identity key (`protocol://host:port/path`).

## File Readiness Strategies (Phase 1 Complete)

- `IFileReadinessChecker` abstraction added.
- Size-stability implemented (`MinStableSeconds`).
- Future enhancements: rename pattern detection, temp extension exclusion, advisory lock probes.

## Configuration & Secrets (Updated)

- `RemoteFileSourcesOptions` with per-source protocol, host, path, credential secret references.
- `PipelineFeaturesOptions` now includes: `EnableLocalPoller`, `EnableFtpPoller`, `EnableSftpPoller`, `EnableFileTransfer`.
- Secret resolution currently uses an in-memory + environment resolver; production to swap with Key Vault implementation.
- Future: introduce Service Bus options (`ServiceBusIngressOptions`, `ServiceBusEgressOptions`).

## Processing Pipeline

- Extend FileProcessingService to: validate, transfer, archive, emit telemetry.
- Introduce IFileTransferService abstraction with protocol-specific implementations.
- Add optional egress publish step (post-success). Make failure to publish configurable (fail pipeline vs best-effort).

## Idempotency / Exactly-Once (Planned)

- Current state: in-memory suppression of duplicates during a process lifetime.
- Planned: Redis-backed registry keyed by identity hash (protocol + host + path + size + mtime) for cross-instance de-dup.
- Extend to guard egress notifications / downstream publishes.

## Validation & Mapping

- Add validators for configuration objects and feature requests.

## Testing

- Add unit tests for Result error flows.
- Add tests for FileProcessingService once behavior added.
- Plan integration tests with a test Redis container.
- Add Service Bus integration tests (emulator or live resource) for ingress translation & egress notification reliability.
- Simulate transient failures (lock lost, timeout) to verify retry and DLQ logic.

## Containerization

- Add Dockerfile (multi-stage) for Host.
- Add docker-compose for local Redis + application.

## Lint / Analyzers

- Add .editorconfig and optional analyzers for style and warnings.

## Security

- Ensure no secrets in code. Use Key Vault references.

---

Generated scaffold prepared for incremental additions.

## Orchestrator (Implemented)

- Background service `FilePipelineBackgroundService` now:
  - Invokes `IFilePoller` once per interval.
  - Drains up to `BatchReadLimit` events from `IFileEventQueue`.
  - Processes each via `IFileProcessingService`.
- Configured via `Polling` options (IntervalMilliseconds, BatchReadLimit) bound in Host.

### Immediate Next Increment Ideas (Refreshed)

1. Persist duplicate suppression across restarts (Redis set or sorted set with TTL).
2. Add rename / temp extension readiness strategy.
3. Parallelize processing stage with bounded degree after queue dequeue (configurable).
4. Introduce archive / retention policy (move processed files to structured archive root with date partitioning).
5. Add more granular telemetry: backoff histogram, per-protocol error breakdown.

### Logging (Implemented Increment)

- Added ILogger injection to: FileProcessingService, InMemoryFileEventQueue, SyntheticFilePoller.
- Levels used:
  - Debug/Trace: routine lifecycle (processing start, dequeue, synthetic event generation)
  - Warning: failures (enqueue rejection, processing failure, poll enqueue failure)
  - Information: queue initialization, service start/stop (background service already had info logs)
- Tests updated to use NullLogger to keep output clean.

### Tech Debt / Follow-Ups

- Replace naive polling delay with adaptive sleep (short circuit if queue still has backlog).
- Consider partial parallel processing (bounded degree) once FileProcessingService performs real I/O.
- Evaluate using `PeriodicTimer` for clarity over Task.Delay loop.
