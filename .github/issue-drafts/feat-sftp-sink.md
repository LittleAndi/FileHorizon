# feat: introduce SFTP sink

Labels: enhancement, integration

## Summary

Add an `IFileSink` implementation that uploads files to an SFTP server supporting directory creation (mkdir -p) and host key verification.

## Motivation

Many legacy or partner integrations rely on SFTP drop zones. Native sink support removes the need for an external transfer stage.

## Acceptance Criteria

- New sink type `SftpFileSink` implementing `IFileSink`.
- Configuration: host, port, username, auth method (password OR private key path / secret ref), host key fingerprints (allow list), root directory, optional upload temp name then atomic rename.
- Validates remote directory existence; creates nested directories as needed.
- Supports streaming upload from reader to remote.
- Emits metrics: `sftp.upload.bytes` (counter), `sftp.upload.duration.ms` (histogram), failures counter.
- Enforces host key verification; connection fails if mismatch.
- Tests with a mock / in-memory SFTP server (future integration) + unit tests using a test double for the SFTP client abstraction.

## Implementation Sketch

1. Add `ISftpClient` abstraction (or reuse existing `IRemoteFileClient` with extension) in `Abstractions`.
2. Implement infrastructure adapter using a lightweight SFTP lib (ONLY if approved; else placeholder + interface).
3. Directory ensure logic (split path, create missing segments).
4. Stream upload with temp filename: `<final>.part` then rename.
5. Host key fingerprint validation before auth.
6. Telemetry integration.

## Security Considerations

- No secrets in config; reference via `ISecretResolver` keys.
- Enforce strong ciphers (document; library-dependent).

## Idempotency

- Overwriting same path should be safe (document). Consider storing checksum to avoid duplicate uploads (future).

## Open Questions

- Support parallel uploads? Possibly later via orchestrator concurrency control.
