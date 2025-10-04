# feat: service bus ingress bridge

Labels: enhancement, integration

## Summary

Bridge Azure Service Bus queue/topic messages into the internal `IFileEventQueue` after validation, enabling external systems to enqueue file processing requests via messaging.

## Motivation

External producers may not have direct network access to shared storage or want asynchronous decoupling. A message-based ingress standardizes integration.

## Acceptance Criteria

- Configurable Service Bus connection (namespace, entity name, subscription for topic).
- Background listener converts messages (JSON) -> internal file event model (validate via `IFileEventValidator`).
- Dead-letter or poison queue routing on validation failure with reason metadata.
- Idempotent ingestion (ignore duplicates via message ID or dedupe token header).
- Metrics: `servicebus.ingress.messages`, `servicebus.ingress.failures`, `servicebus.ingress.dlq`.
- Graceful shutdown honoring in-flight message completion.

## Implementation Sketch

1. Define message contract (doc) and deserializer.
2. Add `IServiceBusListener` abstraction + implementation in Infrastructure (only if library approved; else placeholder).
3. Register hosted service in Host composition.
4. On receive: validate -> enqueue internal queue -> complete; on fail: dead-letter.
5. Telemetry integration + structured logs (messageId, sourceSystem).

## Security

- Connection string secret resolution via `ISecretResolver`.

## Open Questions

- Support sessions? (Maybe later) / Prefetch & concurrency tuning.
