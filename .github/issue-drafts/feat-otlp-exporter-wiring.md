# feat: OTLP exporter wiring

Labels: telemetry, observability

## Summary

Add OpenTelemetry (OTLP) exporter configuration in Host with resource detection and environment-based configuration.

## Motivation

Enable richer distributed traces and metrics to flow into external backends (e.g., Azure Monitor, Grafana Tempo, Honeycomb) without manual code changes.

## Acceptance Criteria

- Host project wires OpenTelemetry for traces + metrics (if not existing) with OTLP exporter toggled via config (`Otel:Enabled`).
- Resource attributes: service.name=FileHorizon, service.version (assembly), deployment.environment.
- Configurable endpoints & protocol (gRPC/http) + headers via secrets.
- Respect existing internal telemetry abstractions; no leakage into Core.
- Docs updated `metrics-and-telemetry-dashboard.md` referencing OTLP usage.
- Graceful shutdown flush.

## Implementation Sketch

1. Add extension in `FileHorizon.Host` to configure OTel pipeline.
2. Bind strongly-typed `OtelOptions` with endpoint, protocol, headers.
3. Conditional registration when enabled.
4. Ensure Prometheus exporter compatibility (either both or either/or documented).
5. Provide sample config snippet.

## Security

- Sensitive headers (auth tokens) resolved via `ISecretResolver` or env vars.

## Open Questions

- Provide trace sampling config now or later? (Maybe default parentBased+alwaysOn)
