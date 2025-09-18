# Next Steps

## Observability (OpenTelemetry)
- Add OpenTelemetry packages (Tracing, Metrics, Logging) in Host only.
- Configure Resource builder with service.name=FileHorizon
- Exporters: Prometheus + OTLP (future). No other logging frameworks.

## Redis Streams Integration
- Add abstractions for Redis stream consumer/producer (already partial via IFileEventQueue).
- Implement Infrastructure/Redis/RedisFileEventQueue using StackExchange.Redis.
- Handle consumer group creation, claim pending messages on startup, ack after processing.

## Pollers for Protocols
- Implement separate pollers: UNC, FTP, SFTP each behind IFilePoller or distinct interfaces (maybe strategy pattern) and orchestrate via a coordinator service.
- Read configuration for sources from Azure App Configuration.

## File Readiness Strategies
- Add strategy interfaces (e.g., IFileReadinessChecker) per protocol.
- Implement size-stable checks, temp-name rename, exclusive lock attempts.

## Configuration & Secrets
- Add options classes in `Configuration/` (e.g., SourceEndpointOptions, RedisOptions).
- Bind in Host from Azure App Configuration + Key Vault references.

## Processing Pipeline
- Extend FileProcessingService to: validate, transfer, archive, emit telemetry.
- Introduce IFileTransferService abstraction with protocol-specific implementations.

## Idempotency / Exactly-Once
- Persist processed file IDs (hash of path + size + lastWrite) in Redis SET or a lightweight store to prevent duplicates.

## Validation & Mapping
- Add validators for configuration objects and feature requests.

## Testing
- Add unit tests for Result error flows.
- Add tests for FileProcessingService once behavior added.
- Plan integration tests with a test Redis container.

## Containerization
- Add Dockerfile (multi-stage) for Host.
- Add docker-compose for local Redis + application.

## Lint / Analyzers
- Add .editorconfig and optional analyzers for style and warnings.

## Security
- Ensure no secrets in code. Use Key Vault references.

---
Generated scaffold prepared for incremental additions.
