# Post-Documentation Update Follow-Up

Recommended GitHub issues to open after syncing architecture & roadmap docs.

| Title                                     | Labels                  | Summary                                                                                                        |
| ----------------------------------------- | ----------------------- | -------------------------------------------------------------------------------------------------------------- |
| feat: implement multi-destination fan-out | enhancement,processing  | Add loop over all routed DestinationPlans with configurable failure policy (strict vs partial).                |
| feat: add per-destination retry policies  | enhancement,reliability | Introduce retry/backoff options section under TransferOptions for sink writes.                                 |
| feat: introduce SFTP sink                 | enhancement,integration | Implement IFileSink for SFTP uploads (upload + mkdir -p semantics, host key verification).                     |
| feat: enhanced idempotency key            | enhancement,idempotency | Derive composite hash (protocol+normalized path+size+mtime+routing fingerprint); migrate existing keys safely. |
| feat: service bus ingress bridge          | enhancement,integration | Bridge Service Bus queue/topic -> internal IFileEventQueue with validation + DLQ.                              |
| feat: service bus egress publisher        | enhancement,integration | Publish file processed notifications with idempotent suppression.                                              |
| feat: router & sink failure metrics       | telemetry,observability | Emit router.matches, router.fanout.count, sink.write.failures with labels.                                     |
| feat: archive / retention stage           | enhancement,processing  | Optional post-success archival (date partitioning) before deletion.                                            |
| feat: checksum integrity option           | enhancement,integrity   | Optional hash compute (e.g., SHA256) at read + verify after write.                                             |
| feat: OTLP exporter wiring                | telemetry,observability | Add OTLP exporter in Host with Resource detection & environment config.                                        |
| chore: adaptive polling loop              | tech-debt,performance   | Replace fixed delay with backlog-aware or PeriodicTimer implementation.                                        |
| chore: secret resolver provider model     | tech-debt,security      | Abstract secret resolution to allow Key Vault provider without code changes.                                   |

## PR Checklist (to copy into future PRs)

- [ ] Updated docs aligned (architecture, smoke-tests, next-steps)
- [ ] Added/adjusted tests for new behavior
- [ ] Metrics / spans updated & documented
- [ ] Feature flags documented (if added)
- [ ] Idempotency impact considered
- [ ] Security review (secrets / logging) performed

---

Keep this file lightweight; revise as roadmap items are delivered.
