# File Processing Architecture (Vision)

This document describes the recommended processing architecture for FileHorizon as sources and destinations expand. It captures the target abstractions, configuration model, scalability characteristics, and a pragmatic migration plan from the current implementation.

## Why change

- Today, processing is tied to local file moves. As we add remote sources (FTP/SFTP) and new destinations (SFTP/Azure Blob/S3), a pairwise processor per protocol quickly becomes unmanageable.
- A single orchestrator with pluggable source readers and destination sinks avoids N×M growth, centralizes resilience and telemetry, and enables fan‑out to multiple destinations.

## High‑level design

- Orchestrator: one implementation of `IFileProcessor` coordinates a transfer for each `FileEvent`.
- Source adapters (readers): open a readable stream regardless of protocol (Local, FTP, SFTP, …).
- Destination adapters (sinks): write a stream to the target (Local, SFTP, Azure Blob, S3, …).
- Router: maps a `FileEvent` to one or more destination plans (fan‑out supported) with per‑plan options (rename, overwrite, checksum).
- Shared policies: retries, backoff, throttling, idempotency, and telemetry applied uniformly.

### Minimal contracts (proposed)

```csharp
namespace FileHorizon.Application.Abstractions;

public interface IFileProcessor
{
    Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct);
}

public interface IFileContentReader // source adapter
{
    Task<Result<Stream>> OpenReadAsync(FileReference file, CancellationToken ct);
    Task<Result<FileAttributesInfo>> GetAttributesAsync(FileReference file, CancellationToken ct);
}

public interface IFileSink // destination adapter
{
    string Name { get; }
    Task<Result> WriteAsync(FileReference target, Stream content, FileWriteOptions options, CancellationToken ct);
}

public interface IFileRouter
{
    Task<Result<IReadOnlyList<DestinationPlan>>> RouteAsync(FileEvent fileEvent, CancellationToken ct);
}

public sealed record FileReference(string Scheme, string? Host, int? Port, string Path, string? SourceName);
public sealed record FileAttributesInfo(long Size, DateTimeOffset LastWriteUtc, string? Hash);
public sealed record FileWriteOptions(bool Overwrite, bool ComputeHash, string? RenamePattern);
public sealed record DestinationPlan(string DestinationName, string TargetPath, FileWriteOptions Options);
```

Notes

- `Scheme` examples: `local`, `ftp`, `sftp`, `blob`, `s3`.
- The orchestrator translates a `FileEvent` into a source `FileReference`, obtains a reader, asks the router for one or more destinations, then streams to each sink.

## Configuration model (proposed)

Split destination configuration from routing rules to avoid duplication and enable reuse.

- DestinationsOptions
  - `LocalDestinations: [ { Name, RootPath } ]`
  - `SftpDestinations: [ { Name, Host, Port, Username, PasswordSecretRef, RootPath, StrictHostKey } ]`
  - `BlobDestinations: [ { Name, ConnectionStringSecretRef | ManagedIdentity, Container, Prefix } ]`
- RoutingOptions
  - `Rules: [ { Name, Match: { SourceName?, Protocol?, PathGlob?, PathRegex? }, Destinations: [names], RenamePattern?, Overwrite?, PostAction?, ArchivePath? } ]`
- TransferOptions
  - `MaxConcurrentPerDestination`, `Retry: { MaxAttempts, BackoffBaseMs, BackoffMaxMs }`, `ChunkSizeBytes`, `Checksum: { Algorithm }`

JSON example

```json
{
  "Destinations": {
    "Local": [
      { "Name": "OutboxA", "RootPath": "/data/outboxA" },
      { "Name": "OutboxC", "RootPath": "/data/outboxC" }
    ],
    "Sftp": [
      {
        "Name": "PartnerOut",
        "Host": "sftp.partner.net",
        "Port": 22,
        "Username": "svc",
        "PasswordSecretRef": "SFTP_OUT_PASS",
        "RootPath": "/out"
      }
    ]
  },
  "Routing": {
    "Rules": [
      {
        "Name": "SftpIn-To-OutboxC",
        "Match": { "SourceName": "PartnerSftp" },
        "Destinations": ["OutboxC"],
        "RenamePattern": "{yyyyMMdd}/{fileName}",
        "Overwrite": false
      },
      {
        "Name": "InboxA-Fanout",
        "Match": { "SourceName": "InboxA", "PathGlob": "**/*.csv" },
        "Destinations": ["OutboxA", "PartnerOut"]
      }
    ]
  }
}
```

## Horizontal scalability

Yes—`IFileProcessor` is intended to be horizontally scalable.

- Work distribution: Use Redis Streams consumer groups (already present). Multiple pods/containers can process in parallel without duplicate delivery.
- Exactly‑once semantics: Implement an idempotency key derived from the file identity (protocol + normalized path + size + mtime + routing fingerprint). Store in Redis (SET or HASH) before side‑effects; treat duplicates as no‑ops.
- ACK discipline: ACK the queue message only after all destination writes succeed and idempotency state is persisted.
- Concurrency control: Apply per‑destination limits (e.g., `MaxConcurrentPerDestination`) to avoid overloading slower sinks.
- Failure isolation: A failure to one destination in a fan‑out either (a) fails the whole event (strict), or (b) records partial success and retries the remaining subset—configurable.
- Leases/timeouts: Use operation timeouts and safe cancellation; no shared in‑memory state is required between nodes.

Edge cases

- Large files: stream in chunks; don’t buffer whole files in memory; consider resumable uploads for large object stores.
- Reprocessing on restart: idempotency registry prevents double‑writes.
- Poison messages: after N retries, dead‑letter (separate stream) with error metadata.

## Telemetry

- Activity: `file.process` spanning the entire orchestrated copy; child spans for `reader.open`, `sink.write` per destination.
- Metrics:
  - `files.processed`, `files.failed`, `bytes.copied` (per destination), `processing.duration.ms` histogram
  - `sink.write.failures` with `destination`, `error.type`
  - `router.matches` and `router.fanout.count`

## Security

- Secrets resolved through `ISecretResolver` (host can bind Azure Key Vault in production).
- Avoid logging credentials/paths with PII; redact sensitive tags.
- Optional strict host key checking for SFTP.

## Migration plan (incremental)

1. Introduce contracts

- Add `IFileContentReader`, `IFileSink`, `IFileRouter`, `IFileProcessor` (orchestrator).
- Provide empty stubs and unit tests for interfaces.

2. Implement first adapters

- Readers: Local, SFTP (re‑use existing SSH.NET client abstraction).
- Sinks: Local (write to filesystem).

3. Simple router

- Rule: map `SourceName` -> `DestinationName` (1:1). Bind minimal `RoutingOptions`.

4. Orchestrator

- Replace the current local transfer processor behind the `IFileProcessor` DI binding with the orchestrator.
- Keep the old class around temporarily, then remove once parity is verified.

5. Idempotency

- Add Redis‑backed store for processed keys; integrate into orchestrator.

6. Fan‑out & retries

- Add support for multiple destinations and per‑destination retry policy.

7. New sinks

- Add SFTP sink, then a cloud object store sink (e.g., Azure Blob) behind feature flags.

8. Telemetry & docs

- Expand metrics/trace coverage, update dashboards and README.

## Performance considerations

- Async, non‑blocking I/O everywhere; avoid `.Result`/`.Wait()`.
- Stream copy using pooled buffers; configurable chunk size.
- Backpressure: throttle by destination; pause dequeue if destination backlog grows beyond thresholds.

## Backward compatibility & rollout

- Feature flags gate new behavior (`EnableFileTransfer`, destination/sink enablement flags if needed).
- Can run orchestrator in shadow mode (read & route only) while still executing the legacy local processor, to verify route decisions via telemetry before switching.

## Open questions

- Rename conventions across destinations (time‑based partitioning, collision handling).
- Partial success policy on fan‑out.
- Checksums: compute at source vs at sink vs end‑to‑end verification.

---

This document serves as the blueprint for implementing the next iteration of processing. It is intentionally modular so we can merge in small PRs without disrupting current stability.
