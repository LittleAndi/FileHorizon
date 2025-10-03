# File Processing Architecture (Vision & Current Status)

This document describes the target processing architecture for FileHorizon and the current implementation state. It captures abstractions, configuration model, scalability characteristics, and a pragmatic migration plan. Sections annotated with (Implemented), (In Progress), or (Planned) reflect repository reality on the `feat/ftp-pollers` branch at the time of this update.

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

Current implemented spans / activities:

- `file.orchestrate` (overall orchestrator execution – current primary span)
- `reader.open` (wrapping content reader open)
- `sink.write` (inside local sink write)

Planned / not yet emitted:

- `file.process` (parent umbrella span if we introduce a higher-level service boundary)
- `router.route` (to measure routing latency)
- Additional destination-specific spans once multiple sinks / fan-out arrive.

Implemented metrics (see `TelemetryInstrumentation`):

- `files.processed`
- `files.failed`
- `bytes.copied`
- `processing.duration.ms` (histogram)
- `poll.cycles`, `poll.cycle.duration.ms`
- `files.discovered`, `files.skipped.unstable`
- Queue metrics: `queue.enqueued`, `queue.dequeued`, `queue.enqueue.failures`, `queue.dequeue.failures`

Not yet implemented (previously listed aspirationally):

- `sink.write.failures` (would distinguish failure categories per sink)
- `router.matches`
- `router.fanout.count`

These remain in the roadmap and will be added once multi-destination routing is implemented.

## Security

- Secrets resolved through `ISecretResolver` (host can bind Azure Key Vault in production).
- Avoid logging credentials/paths with PII; redact sensitive tags.
- Optional strict host key checking for SFTP.

## Migration plan (incremental)

| Step | Description                                                                              | Status      | Notes                                                                            |
| ---- | ---------------------------------------------------------------------------------------- | ----------- | -------------------------------------------------------------------------------- |
| 1    | Introduce contracts (`IFileContentReader`, `IFileSink`, `IFileRouter`, `IFileProcessor`) | Implemented | Interfaces present & used in DI                                                  |
| 2    | First adapters (Local reader & sink, SFTP reader)                                        | Implemented | Local sink + local & SFTP readers registered                                     |
| 3    | Simple router (1:1 SourceName -> Destination)                                            | Implemented | `SimpleFileRouter` returns first matching plan                                   |
| 4    | Orchestrator replaces legacy processor                                                   | Implemented | `FileProcessingOrchestrator` registered as sole `IFileProcessor`; legacy removed |
| 5    | Idempotency (Redis + in‑memory fallback)                                                 | Implemented | Key = `file:{FileEvent.Id}`; future richer key planned                           |
| 6    | Fan‑out & retries                                                                        | Planned     | Current orchestrator processes first destination only                            |
| 7    | New sinks (SFTP, Blob, etc.)                                                             | Planned     | Only Local sink implemented; SFTP is reader-only                                 |
| 8    | Expanded telemetry (router.\*, sink failure metrics)                                     | Planned     | Metrics subset live; additional counters pending fan‑out                         |

### Current limitations

- Single destination per file event (first route plan only).
- No per-destination retry abstraction yet (errors propagate immediately).
- Idempotency key is simplistic (event ID); routing/destination fingerprint not yet included.
- No cross-destination fan-out or partial success policy.
- Only local writes; remote destination sinks are future work.

## Performance considerations

- Async, non‑blocking I/O everywhere; avoid `.Result`/`.Wait()`.
- Stream copy using pooled buffers; configurable chunk size.
- Backpressure: throttle by destination; pause dequeue if destination backlog grows beyond thresholds.

## Backward compatibility & rollout

- Feature flags gate new behavior (`EnableFileTransfer`, destination/sink enablement flags if needed).
- Can run orchestrator in shadow mode (read & route only) while still executing the legacy local processor, to verify route decisions via telemetry before switching.

## Open questions (carried forward)

- Rename conventions across destinations (time‑based partitioning, collision handling) – Deferred design.
- Partial success policy on fan‑out – To be decided alongside fan‑out implementation.
- Checksums: compute at source vs at sink vs end‑to‑end verification – TBD (will tie into integrity metrics & idempotency enhancement).

## Verification matrix

| Concern                                     | Implemented? | Reference                                     | Next Step                                  |
| ------------------------------------------- | ------------ | --------------------------------------------- | ------------------------------------------ |
| Routing (single destination)                | Yes          | `SimpleFileRouter`                            | Add multi-destination fan-out              |
| Idempotency (in-memory/Redis)               | Yes          | `IdempotencyOptions`, `RedisIdempotencyStore` | Enhance key with file identity hash        |
| Telemetry basic metrics                     | Yes          | `TelemetryInstrumentation`                    | Add router & sink failure metrics          |
| Activity spans (orchestrate/read/write)     | Yes          | Orchestrator & Local sink                     | Introduce higher-level `file.process` span |
| SFTP reader                                 | Yes          | `SftpFileContentReader`                       | Add SFTP sink implementation               |
| FTP poller & deletion                       | In Progress  | `FtpPoller`, orchestrator deletion logic      | Add FTP reader & sink if needed            |
| Fan-out                                     | No           | N/A                                           | Implement multi-destination write loop     |
| Retry policy (writes)                       | No           | N/A                                           | Introduce configurable retry/backoff       |
| Archive / retention                         | No           | N/A                                           | Add post-write archive step                |
| Service Bus ingress/egress                  | No           | N/A                                           | Implement as async bridges                 |
| Checksum / integrity                        | No           | N/A                                           | Add optional hash compute & validation     |
| Sink abstraction for cloud                  | No           | N/A                                           | Implement Blob/S3 sinks                    |
| Extended metrics (router.\*, sink.failures) | No           | N/A                                           | Emit after fan-out                         |

---

This document serves as the blueprint for implementing the next iteration of processing. It is intentionally modular so we can merge in small PRs without disrupting current stability.
