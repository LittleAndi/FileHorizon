# Next Steps

## Observability (OpenTelemetry)

Status: Partially Implemented

- Host exposes Prometheus metrics via OpenTelemetry Meter (implemented metrics listed in `TelemetryInstrumentation`).
- Tracing activities emitted: `file.orchestrate`, `reader.open`, `sink.write`.
- Pending: add OpenTelemetry exporter wiring (OTLP exporter), structured logging via OTEL, additional spans (router.\*, fan-out, retries).

## Redis Streams Integration

Status: Implemented (baseline)

- `RedisFileEventQueue` implemented with consumer group option.
- Fallback to in-memory queue when Redis disabled or unavailable.
- Pending Enhancements: claiming pending messages on startup (partial), dead-letter stream for poison events, visibility timeout enforcement.

## Azure Service Bus Ingress / Egress (External Integration)

- Implement `ServiceBusIngressListener` that reads from a queue/topic and translates messages into internal `IFileEventQueue` entries (Redis Streams writes).
- Validate and enrich incoming commands (e.g., resolve source protocol + path) before enqueue.
- Dead-letter malformed or repeatedly failing ingress messages with diagnostic metadata.
- Implement `ServiceBusNotificationPublisher` invoked after successful file processing (hook inside FileProcessingService or post-processing pipeline decorator).
- Publish minimal metadata (file id, size, checksum, status, archive location) to a topic for downstream subscribers.
- Retry transient failures; send to dead-letter (or fallback log) on permanent failure.
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

## Idempotency / Exactly-Once

Status: Implemented (Initial) / Planned (Enhanced)

- Current: In-memory or Redis-backed (if enabled) key = `file:{FileEvent.Id}`.
- Planned: richer identity hash (protocol + host + path + size + mtime + routing fingerprint) for cross-instance de-dup and guarding egress notifications.
- Future: exactly-once semantics across multi-destination fan-out + egress publishes.

## Validation & Mapping

- Add validators for configuration objects and feature requests.

## Testing

- Add unit tests for Result error flows.
- Add tests for FileProcessingService once behavior added.
- Plan integration tests with a test Redis container.
- Add Service Bus integration tests (emulator or live resource) for ingress translation & egress notification reliability.
- Simulate transient failures (lock lost, timeout) to verify retry and DLQ logic.

## Containerization

Status: Implemented (Dockerfile + docker-compose present)

- Pending: multi-arch build optimization, slim base image hardening, health check integration for poller & worker separation.

## Lint / Analyzers

- Add .editorconfig and optional analyzers for style and warnings.

## Security

- Ensure no secrets in code. Use Key Vault references.

---

---

## Completed (Historical)

| Area                  | Summary                                                                             |
| --------------------- | ----------------------------------------------------------------------------------- |
| Pollers               | Local, FTP, SFTP pollers behind `IFilePoller`; composite multiprotocol poller.      |
| Orchestrator          | `FileProcessingOrchestrator` replaces legacy local processor.                       |
| Local Sink            | Filesystem write with basic overwrite + rename pattern support.                     |
| SFTP Reader           | SFTP source reading supported (no SFTP sink yet).                                   |
| Redis Queue           | Pluggable Redis Streams queue with fallback to in-memory.                           |
| Idempotency (Phase 1) | In-memory + Redis store keyed by event Id.                                          |
| Metrics (Baseline)    | Processing, queue, polling, bytes copied counters + basic histograms.               |
| Config Model          | Destinations, Routing, Transfer, Remote sources option classes with validation.     |
| Deletion Support      | Post-transfer deletion for local, SFTP, FTP sources (where credentials resolvable). |

## Roadmap Status Table

| Item                              | Status  | Next Action                                              |
| --------------------------------- | ------- | -------------------------------------------------------- |
| Fan-out (multi-destination)       | Planned | Implement destination loop + error policy                |
| Per-destination retries           | Planned | Introduce retry abstraction & options                    |
| SFTP Sink                         | Planned | Implement write & host key validation                    |
| Cloud Object Store Sink (Blob/S3) | Planned | Add first cloud sink behind feature flag                 |
| Enhanced Idempotency Key          | Planned | Derive deterministic hash & migrate store keys           |
| Service Bus Ingress               | Planned | Bridge queue -> internal events with validation          |
| Service Bus Egress                | Planned | Publish notifications post-success with idempotent guard |
| Router Metrics & fan-out counters | Planned | Emit `router.matches`, `router.fanout.count`             |
| Sink failure metrics              | Planned | Add `sink.write.failures` with error classification      |
| Checksum / Integrity              | Planned | Optional hash compute + verification step                |
| Archive / Retention               | Planned | Post-write archival pipeline stage                       |
| Extended Readiness Strategies     | Planned | Temp extension exclusion, rename detection               |
| OTLP Exporter                     | Planned | Wire OTLP span & metric exporter in Host                 |
| Security Hardening                | Planned | Key Vault secret resolver implementation                 |
| Parallel Processing (bounded)     | Planned | Introduce limited concurrency per worker                 |

---

## Orchestrator (Implemented)

- Background service `FilePipelineBackgroundService` now:
  - Invokes `IFilePoller` once per interval.
  - Drains up to `BatchReadLimit` events from `IFileEventQueue`.
  - Processes each via `IFileProcessingService`.
- Configured via `Polling` options (IntervalMilliseconds, BatchReadLimit) bound in Host.

### Immediate Next Increment Ideas (Refreshed)

1. Multi-destination fan-out support (write loop + failure policy decision: all-or-nothing vs partial retry).
2. Enhanced idempotency key (include identity hash) + migration path.
3. Sink failure metric & router.matches counter.
4. Per-destination retry/backoff configuration.
5. Service Bus ingress bridge (scaffold + feature flag).
6. Archive / retention pipeline stage (optional, post-success).

Secondary (after above): readiness enhancements, checksum option, OTLP exporter wiring.

### Logging (Implemented Increment)

- Added ILogger injection to: FileProcessingService, InMemoryFileEventQueue, SyntheticFilePoller.
- Levels used:
  - Debug/Trace: routine lifecycle (processing start, dequeue, synthetic event generation)
  - Warning: failures (enqueue rejection, processing failure, poll enqueue failure)
  - Information: queue initialization, service start/stop (background service already had info logs)
- Tests updated to use NullLogger to keep output clean.

### Tech Debt / Follow-Ups

- Adaptive polling delay (short-circuit when backlog remains).
- Bounded parallelism for processing (once multi-destination implemented).
- Replace delay loops with `PeriodicTimer` for clarity.
- Consolidate secret resolution to allow pluggable providers (Key Vault, AWS Secrets Manager).

---

Generated scaffold updated to reflect current implementation and forward plan.
