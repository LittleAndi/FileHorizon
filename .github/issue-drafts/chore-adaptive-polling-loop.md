# chore: adaptive polling loop

Labels: tech-debt, performance

## Summary

Replace fixed-delay polling with backlog-aware adaptive loop or `PeriodicTimer`-based implementation to improve responsiveness and reduce idle CPU.

## Motivation

Static sleep intervals either waste time (too long) or thrash resources (too short). Adaptive scheduling based on backlog size or recent activity enhances efficiency.

## Acceptance Criteria

- Polling mechanism dynamically adjusts delay within configured min/max bounds based on: recent file discoveries, queue backlog depth, or consecutive idle cycles.
- Provide configuration: `Polling:MinDelayMs`, `Polling:MaxDelayMs`, `Polling:IdleBackoffFactor`.
- CPU usage decreases in idle scenarios (document heuristics) while latency for new files remains comparable.
- Unit tests for delay selection logic edge cases.

## Implementation Sketch

1. Extract polling loop into strategy class (pure logic returning next delay).
2. Introduce state struct tracking consecutive idle cycles + backlog size.
3. Integrate with existing pollers (local, remote) via abstraction.
4. Add metrics: `poller.idle.cycles`, `poller.dynamic.delay.ms` (histogram).

## Risks

- Overfitting heuristics; keep simple and configurable.

## Open Questions

- Should we allow plugin for custom strategy? (Future)
