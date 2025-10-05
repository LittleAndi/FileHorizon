# Post-Documentation Update Follow-Up

Recommended GitHub issues to open after syncing architecture & roadmap docs.

| Title                                 | Labels                  | Summary                                                                                    |
| ------------------------------------- | ----------------------- | ------------------------------------------------------------------------------------------ |
| feat: introduce SFTP sink             | enhancement,integration | Implement IFileSink for SFTP uploads (upload + mkdir -p semantics, host key verification). |
| feat: service bus ingress bridge      | enhancement,integration | Bridge Service Bus queue/topic -> internal IFileEventQueue with validation + DLQ.          |
| feat: router & sink failure metrics   | telemetry,observability | Emit router.matches, router.fanout.count, sink.write.failures with labels.                 |
| feat: archive / retention stage       | enhancement,processing  | Optional post-success archival (date partitioning) before deletion.                        |
| chore: secret resolver provider model | tech-debt,security      | Abstract secret resolution to allow Key Vault provider without code changes.               |

## PR Checklist (to copy into future PRs)

- [ ] Updated docs aligned (architecture, smoke-tests, next-steps)
- [ ] Added/adjusted tests for new behavior
- [ ] Metrics / spans updated & documented
- [ ] Feature flags documented (if added)
- [ ] Idempotency impact considered
- [ ] Security review (secrets / logging) performed

---

Keep this file lightweight; revise as roadmap items are delivered.
