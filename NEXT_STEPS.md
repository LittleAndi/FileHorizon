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

## Pollers for Protocols
- Implement separate pollers: UNC, FTP, SFTP each behind IFilePoller or distinct interfaces (maybe strategy pattern) and orchestrate via a coordinator service.
- Read configuration for sources from Azure App Configuration.

## File Readiness Strategies
- Add strategy interfaces (e.g., IFileReadinessChecker) per protocol.
- Implement size-stable checks, temp-name rename, exclusive lock attempts.

## Configuration & Secrets
- Add options classes in `Configuration/` (e.g., SourceEndpointOptions, RedisOptions).
- Bind in Host from Azure App Configuration + Key Vault references.
 - Extend with Service Bus: `ServiceBusIngressOptions`, `ServiceBusEgressOptions`, flags for enablement.

## Processing Pipeline
- Extend FileProcessingService to: validate, transfer, archive, emit telemetry.
- Introduce IFileTransferService abstraction with protocol-specific implementations.
- Add optional egress publish step (post-success). Make failure to publish configurable (fail pipeline vs best-effort).

## Idempotency / Exactly-Once
- Persist processed file IDs (hash of path + size + lastWrite) in Redis SET or a lightweight store to prevent duplicates.
- Reuse id registry to avoid duplicate egress notifications (store last published marker or checksum).

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
