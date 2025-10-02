# FileHorizon

[![CI](https://github.com/LittleAndi/FileHorizon/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/LittleAndi/FileHorizon/actions/workflows/ci.yml)

> Disclaimer: A significant portion of this project's code is intentionally authored with AI assistance (pair‑programming style) via pull requests that still pass through normal version control, code review, and CI quality gates. All generated contributions are curated, adjusted, and ultimately owned by the repository maintainer. If you spot something that can be improved, please open an issue or PR.

**FileHorizon** is an open-source, container-ready file transfer and orchestration system. Designed as a modern alternative to heavyweight integration platforms, it provides a lightweight yet reliable way to move files across **local/UNC paths, FTP, and SFTP** while ensuring observability and control. By leveraging **Redis** for distributed coordination, FileHorizon can scale out to multiple parallel containers without duplicate processing, making it suitable for both on-premises and hybrid cloud deployments.

Configuration is centralized through **Azure App Configuration** and **Azure Key Vault**, enabling secure, dynamic management of connections and destinations. With **OpenTelemetry** at its core, FileHorizon delivers unified **logging, metrics, and tracing** out of the box—no separate logging stack required. The system emphasizes **safety and consistency**, ensuring files are only picked up once they are fully written at the source.

FileHorizon is built for teams that need the reliability of managed file transfer (MFT) but want the flexibility, transparency, and scalability of modern open-source tooling.

## Container Image

This repository includes a multi-stage `Dockerfile` for building a lean runtime image that runs as a non-root user.

> WSL / containerd environments: If you're on **WSL2 using Rancher Desktop / Lima / containerd**, prefer `nerdctl` over the classic `docker` CLI. The examples below show both forms where it matters. Mixing `docker` (Moby) and `nerdctl` (containerd) against different runtimes in the same workspace can produce confusing state (images not found, networks missing, etc.). Pick one consistently—on WSL + containerd choose `nerdctl`.

### Build

```
docker build -t filehorizon:dev .
```

Optional build args:

- `BUILD_CONFIGURATION` (default `Release`)
- `UID` / `GID` to align container user with host filesystem permissions

### Run (example)

```
docker run --rm \
	-p 8080:8080 \
	-e ASPNETCORE_ENVIRONMENT=Development \
	-v C:/Temp/FileHorizon/InboxA:/data/inboxA:ro \
	-v C:/Temp/FileHorizon/OutboxA:/data/outboxA \
	filehorizon:dev
```

Health endpoint:

```
curl http://localhost:8080/health
```

### Non-Root Execution

The image defines user `appuser` (UID 1001 by default). If you need to write into mounted volumes, ensure host directories grant appropriate permissions. Override UID/GID at build time if integrating with existing volume ownership models.

### Configuration (High-Level)

Runtime configuration is provided via `appsettings.json` / environment variables. To override via environment variables, use the standard ASP.NET Core naming pattern, e.g.:

```
docker run --rm -p 8080:8080 \
	-e "Pipeline__Role=All" \
	-e "Features__EnableFileTransfer=true" \
	filehorizon:dev
```

---

## Pollers & Feature Flags

File discovery is handled by protocol-specific pollers composed by a multi-protocol coordinator. Each poller can be toggled independently via feature flags so you can roll out new protocols safely.

Feature flags (section `Features`):

| Flag                 | Default | Purpose                                                                                           |
| -------------------- | ------- | ------------------------------------------------------------------------------------------------- |
| `EnableLocalPoller`  | `true`  | Enable local/UNC directory polling sources configured under `FileSources` (legacy/local).         |
| `EnableFtpPoller`    | `false` | Enable FTP remote sources listed in `RemoteFileSources:Sources`.                                  |
| `EnableSftpPoller`   | `false` | Enable SFTP remote sources listed in `RemoteFileSources:Sources`.                                 |
| `EnableFileTransfer` | `false` | Perform the actual transfer/move (side effects). When `false`, pipeline simulates discovery only. |

<!-- Orchestrated processor is now the default; no flag required. -->

Environment variable examples:

```
Features__EnableLocalPoller=true
Features__EnableFtpPoller=false
Features__EnableSftpPoller=true
Features__EnableFileTransfer=true
```

If all three poller flags are disabled the composite poller runs with an empty set (harmless no-op). This is useful for staging environments while only processing already enqueued events.

---

## Remote Source Configuration

Remote (FTP/SFTP) directories are defined under the `RemoteFileSources` options section. Each source entry specifies protocol, connection details, remote path, and readiness behavior.

Example `appsettings.json` excerpt:

```json
{
  "RemoteFileSources": {
    "Sources": [
      {
        "Name": "UpstreamFtp",
        "Protocol": "Ftp",
        "Host": "ftp.example.com",
        "Port": 21,
        "RemotePath": "/drop",
        "UsernameSecret": "secrets:ftp-user",
        "PasswordSecret": "secrets:ftp-pass",
        "MinStableSeconds": 5
      },
      {
        "Name": "PartnerSftp",
        "Protocol": "Sftp",
        "Host": "sftp.partner.net",
        "Port": 22,
        "RemotePath": "/inbound",
        "UsernameSecret": "secrets:sftp-user",
        "PasswordSecret": "secrets:sftp-pass",
        "PrivateKeySecret": "secrets:sftp-key",
        "PrivateKeyPassphraseSecret": "secrets:sftp-key-pass",
        "MinStableSeconds": 8
      }
    ]
  }
}
```

Environment variable form (first FTP source):

```
RemoteFileSources__Sources__0__Name=UpstreamFtp
RemoteFileSources__Sources__0__Protocol=Ftp
RemoteFileSources__Sources__0__Host=ftp.example.com
RemoteFileSources__Sources__0__Port=21
RemoteFileSources__Sources__0__RemotePath=/drop
RemoteFileSources__Sources__0__UsernameSecret=secrets:ftp-user
RemoteFileSources__Sources__0__PasswordSecret=secrets:ftp-pass
RemoteFileSources__Sources__0__MinStableSeconds=5
```

Validation rules enforced at startup:

- `Name`, `Protocol`, `Host`, `RemotePath` required.
- `Port` must be > 0.
- Appropriate credential secret(s) must be present (username/password or key for SFTP; username/password for FTP).
- `MinStableSeconds` must be >= 0.

### Secrets

Secrets are referenced indirectly (`UsernameSecret`, `PasswordSecret`, etc.). The application resolves them through an `ISecretResolver` abstraction. In development a simple in-memory + environment variable resolver is used; production hosts should plug in Azure Key Vault (or alternative) without changing poller code.

### Readiness (Size Stability)

Files are only enqueued once their size remains stable for `MinStableSeconds`. A per-file observation snapshot retains last size & timestamp; unstable files are skipped (counted in metrics) until stable. This minimizes partial file ingestion.

### Backoff & Resilience

Each remote source tracks consecutive failures. An exponential backoff (base 5s doubling up to 5 minutes) delays subsequent attempts after errors (connection, auth, listing). A single success resets the backoff window.

---

## Orchestrated Processing and Routing (New)

The orchestrated processor is enabled by default. It uses a modular orchestrator that selects a protocol-specific reader, routes the file via rules, and writes to a destination sink. This unlocks multi-destination routing and a clean separation of concerns.

Key pieces:

- Readers: `local`, `sftp` (FTP reader pending). The orchestrator selects by `FileReference.Protocol`.
- Router: Matches by protocol and path into a single destination (current implementation is 1:1).
- Sinks: Currently local filesystem sink; remote sinks can be added later.
- Idempotency: Prevents duplicate processing (in-memory or Redis-backed store).

### Configuration: Destinations + Routing + Transfer

Example `appsettings.json` excerpt:

```json
{
  "Destinations": {
    "Local": [
      {
        "Name": "OutboxA",
        "BasePath": "/data/outboxA",
        "Overwrite": true
      }
    ],
    "Sftp": [
      {
        "Name": "PartnerX",
        "Host": "sftp.partner.net",
        "Port": 22,
        "RemotePath": "/outbound",
        "UsernameSecret": "secrets:sftp-user",
        "PasswordSecret": "secrets:sftp-pass"
      }
    ]
  },
  "Routing": {
    "Rules": [
      {
        "Match": {
          "Protocol": "local",
          "PathPattern": "^/data/inboxA/.+\\.txt$"
        },
        "Destination": "OutboxA"
      }
    ]
  },
  "Transfer": {
    "ChunkSizeBytes": 32768,
    "Idempotency": {
      "Enabled": true,
      "TtlSeconds": 86400
    }
  }
}
```

Environment variable form (Windows PowerShell examples):

```
Features__EnableOrchestratedProcessor=true

Destinations__Local__0__Name=OutboxA
Destinations__Local__0__BasePath=/data/outboxA
Destinations__Local__0__Overwrite=true

Routing__Rules__0__Match__Protocol=local
Routing__Rules__0__Match__PathPattern=^/data/inboxA/.+\.txt$
Routing__Rules__0__Destination=OutboxA

Transfer__ChunkSizeBytes=32768
Transfer__Idempotency__Enabled=true
Transfer__Idempotency__TtlSeconds=86400
```

Notes:

- On Windows, paths are normalized internally; the router matches against a normalized forward-slash path.
- If Redis is enabled (`Redis__Enabled=true`), the idempotency store uses Redis with TTL; otherwise an in-memory store is used.
- Current sink support is local filesystem; remote sinks may be added in future.

For a deeper overview see `docs/processing-architecture.md`.

---

## Identity & De‑Duplication

All files (local and remote) are assigned a normalized identity key:

```
<protocol>://<host>:<port>/<normalized/path>
```

Local paths use a normalized form (e.g. `local://_/:0/drive/path/file.txt`). This shared scheme powers duplicate suppression and telemetry tagging.

---

## Additional Telemetry (Polling)

Poller metrics (Meter `FileHorizon`):

| Metric                                      | Type    | Description                               | Key Tags                           |
| ------------------------------------------- | ------- | ----------------------------------------- | ---------------------------------- |
| `filehorizon.poller.poll_cycles`            | Counter | Number of poll cycles executed per source | `protocol`, `source`               |
| `filehorizon.poller.files.discovered`       | Counter | Files discovered (ready)                  | `protocol`, `source`               |
| `filehorizon.poller.files.skipped.unstable` | Counter | Files skipped due to instability          | `protocol`, `source`               |
| `filehorizon.poller.errors`                 | Counter | Poll errors (enumeration failures)        | `protocol`, `source`, `error.type` |

Traces include an Activity per remote source (`poll.source`) with tags: `protocol`, `host`, `source.name`, `backoff.ms` (if applied).

---

### Using docker-compose (nerdctl compatible)

A `docker-compose.yml` is provided to spin up Redis + the FileHorizon app quickly. The file is compatible with `nerdctl compose` in containerd environments (Rancher Desktop, Lima, etc.).

#### If you are on WSL + Rancher Desktop (containerd)

Use `nerdctl compose` (NOT `docker compose`). Example mapping:

| Purpose                 | Docker CLI                           | nerdctl                               |
| ----------------------- | ------------------------------------ | ------------------------------------- |
| Build & up (foreground) | `docker compose up --build`          | `nerdctl compose up --build`          |
| Detached                | `docker compose up -d --build`       | `nerdctl compose up -d --build`       |
| Scale                   | `docker compose up -d --scale app=2` | `nerdctl compose up -d --scale app=2` |
| Logs                    | `docker compose logs -f app`         | `nerdctl compose logs -f app`         |
| Stop                    | `docker compose down`                | `nerdctl compose down`                |

Why: Rancher Desktop (containerd backend) manages images separately from Docker Desktop (Moby). If you run `docker build` then `nerdctl compose up`, the image may not exist in containerd and the deployment will fail. Always build with the same tool:

```
nerdctl build -t filehorizon:dev .
nerdctl compose up -d --build
```

Quick start (nerdctl):

```
# Create local data folders (Linux/macOS examples)
mkdir -p _data/inboxA _data/outboxA

# Or on PowerShell (Windows):
New-Item -ItemType Directory -Path _data/inboxA,_data/outboxA | Out-Null

# Build and start (foreground)
nerdctl compose up --build

# Or start detached
nerdctl compose up -d --build

# Check service status
nerdctl compose ps

# Tail logs
nerdctl compose logs -f app
```

Health check:

```
curl http://localhost:8080/health
```

Stop & remove:

```
nerdctl compose down
```

#### Environment Variables in Compose

The compose file sets sensible defaults:

- `Redis__Enabled=true` enables Redis Streams queue (falls back to in-memory if false).
- `FileSources__Sources__0__*` defines the first file source (InboxA). Add more sources incrementally:
  - `FileSources__Sources__1__Name=InboxB`
  - `FileSources__Sources__1__Path=/data/inboxB`
- Change `Features__EnableFileTransfer` to `true` to perform actual file transfers once implemented.
  Pipeline orchestration now uses `Pipeline__Role` to determine which background services run (see section below). `Features__EnableFileTransfer` only controls whether real file movement occurs.

You can override any value using an `.env` file placed next to `docker-compose.yml`:

```
# .env example
PIPELINE__ROLE=All
FEATURES__ENABLEFILETRANSFER=false
REDIS__ENABLED=true
POLLING__INTERVALMILLISECONDS=500
```

(Compose automatically loads `.env`; ensure variable names match exactly.)

#### Common Adjustments

- Faster polling during development:
  - `Polling__IntervalMilliseconds=500`
- Larger batch processing:
  - `Polling__BatchReadLimit=25`
- Switch to local fallback queue:
  - `Redis__Enabled=false`
- Separate stream per environment:
  - `Redis__StreamName=filehorizon:dev:file-events`

#### Scaling Out (Preview)

To test horizontal scaling (Redis-backed queue required):

```
nerdctl compose up -d --build --scale app=2
```

Each replica will create a unique consumer name derived from `Redis__ConsumerNamePrefix` ensuring cooperative consumption via the shared consumer group.
For a clean separation:

Example: one poller + multiple workers

```
# poller (enqueue only, no processing)
Pipeline__Role=Poller
Features__EnableFileTransfer=false

# worker (process only)
Pipeline__Role=Worker
Features__EnableFileTransfer=true
```

Alternatively (common simpler pattern):

```
# single poller that also processes
Pipeline__Role=All
Features__EnableFileTransfer=true

# additional workers (no polling - processing only)
Pipeline__Role=Worker
Features__EnableFileTransfer=true
```

> Reminder (WSL + containerd): If you previously built with `docker build`, rebuild with `nerdctl build` to ensure the image exists in the containerd image store before scaling.

#### Generating files

Use these commands (or similar) to generate lots of files quickly in the inboxes.

```
seq 1 1000 | xargs -I{} -P 8 sh -c 'echo "test" > inboxA/file{}.txt'
seq 1 1000 | xargs -I{} -P 8 sh -c 'echo "test" > inboxB/file{}.txt'
```

To count files i a folder you can use

```
find . -maxdepth 1 -type f | wc -l
```

### Future Hardening Ideas

- Switch to distroless or `-alpine` base (after validating native dependencies).
- Add read-only root filesystem (`--read-only`) with tmpfs mounts for transient storage.
- Introduce health/liveness/readiness probes in orchestration environments.

---

## Roadmap (Excerpt)

Recently completed (this branch): FTP & SFTP pollers, feature flags, remote readiness/backoff, poller telemetry.

Upcoming / still planned:

- Persistent idempotency & deduplication registry (Redis / durable store)
- Message claiming / retry hardening for Redis Streams
- Service Bus ingress / egress bridge
- Extended telemetry (error categorization, size histograms)
- Configurable secret resolver (production Key Vault implementation)
- Optional archive / checksum verification stage

---

## Observability (OpenTelemetry)

FileHorizon ships with unified tracing, metrics, and structured logging via **OpenTelemetry**. No other logging framework is used.

### What Is Collected

- Traces: file processing spans (`file.process`, `file.orchestrate`), reader/sink spans (`reader.open`, `sink.write`), queue enqueue/dequeue spans (`queue.enqueue`, `queue.dequeue`), lifecycle span (`pipeline.lifetime`).
- Metrics (Meter `FileHorizon`):
  - `files.processed` (counter)
  - `files.failed` (counter)
  - `bytes.copied` (counter)
  - `queue.enqueued` (counter)
  - `queue.enqueue.failures` (counter)
  - `queue.dequeued` (counter)
  - `queue.dequeue.failures` (counter)
  - `processing.duration.ms` (histogram)
  - `poll.cycle.duration.ms` (histogram)

### Prometheus Endpoint

If enabled (default), metrics are exposed at `GET /metrics` using the Prometheus scrape format. Health remains at `/health`.

### Configuration

`Telemetry` section (appsettings or env variables):

| Key                     | Default                | Description                                                    |
| ----------------------- | ---------------------- | -------------------------------------------------------------- |
| `EnableTracing`         | true                   | Enable Activity/trace pipeline                                 |
| `EnableMetrics`         | true                   | Enable metrics collection                                      |
| `EnableLogging`         | true                   | Route structured logs through OTEL exporter                    |
| `EnablePrometheus`      | true                   | Expose `/metrics` endpoint                                     |
| `EnableOtlpExporter`    | false                  | Enable OTLP exporter for traces/metrics/logs                   |
| `OtlpEndpoint`          | null                   | OTLP/gRPC or HTTP endpoint (e.g. `http://otel-collector:4317`) |
| `OtlpHeaders`           | null                   | Additional OTLP headers (key=value;key2=value2)                |
| `OtlpInsecure`          | false                  | Allow insecure (no TLS) if collector enforces it               |
| `ServiceName`           | FileHorizon            | Override service.name resource attribute                       |
| `ServiceVersion`        | Assembly version       | Override service.version                                       |
| `DeploymentEnvironment` | ASPNETCORE_ENVIRONMENT | Adds `deployment.environment` attribute                        |

Environment variable mapping uses double underscores, e.g.:

```
Telemetry__EnableOtlpExporter=true
Telemetry__OtlpEndpoint=http://otel-collector:4317
Telemetry__DeploymentEnvironment=dev
```

### Enabling OTLP Export

Point to a collector (recommended) rather than vendors directly:

```
Telemetry__EnableOtlpExporter=true
Telemetry__OtlpEndpoint=http://otel-collector:4317
```

If headers are required (HTTP/Protobuf variant):

```
Telemetry__OtlpHeaders=api-key=XYZ123
```

### Docker Compose Example (Prometheus + Collector)

Add a collector and Prometheus service (sketch):

```yaml
	otel-collector:
		image: otel/opentelemetry-collector:latest
		command: ["--config=/etc/otel-collector-config.yaml"]
		volumes:
			- ./otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro
		ports:
			- "4317:4317" # OTLP gRPC

	prometheus:
		image: prom/prometheus:latest
		volumes:
			- ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
		ports:
			- "9090:9090"
```

The application container only needs relevant environment variables; `/metrics` will be scraped by Prometheus.

### Log Export

Currently logs go through the OpenTelemetry logging provider. If an OTLP exporter is enabled they will be forwarded to the collector; otherwise they remain local (console).

### Tag / Attribute Conventions (Initial)

| Span                    | Key                             | Example                   |
| ----------------------- | ------------------------------- | ------------------------- |
| file.process            | `file.id`                       | `f_123`                   |
| file.process            | `file.protocol`                 | `local`                   |
| file.process            | `file.source_path`              | `/data/inbox/file1.txt`   |
| file.process            | `file.size_bytes`               | `2048`                    |
| reader.open             | `file.protocol`                 | `sftp`                    |
| sink.write              | `sink.name`                     | `OutboxA`                 |
| queue.enqueue / dequeue | `messaging.system`              | `redis`                   |
| queue.enqueue / dequeue | `messaging.destination`         | `filehorizon:file-events` |
| queue.dequeue           | `messaging.batch.message_count` | `10`                      |

These may evolve toward official semantic conventions as they stabilize.

### Viewing Metrics Locally

```
curl http://localhost:8080/metrics | head -n 40
```

You should see counters like `files_processed_total` and `processing_duration_ms_bucket` (Prometheus histogram exposition).

### Troubleshooting

| Issue                  | Likely Cause           | Remedy                                                |
| ---------------------- | ---------------------- | ----------------------------------------------------- |
| No metrics at /metrics | Prometheus disabled    | Set `Telemetry__EnablePrometheus=true`                |
| No traces in collector | OTLP exporter disabled | Set `Telemetry__EnableOtlpExporter=true` and endpoint |
| Service name wrong     | Override provided      | Adjust `Telemetry__ServiceName`                       |
| High cardinality risk  | Dynamic paths tagged   | Consider trimming or hashing path tags in future      |

---

## Future Telemetry Enhancements

Planned / candidate improvements:

- File size distribution histogram
- Error categorization with semantic convention attributes (e.g. `error.type`)
- Redis pending / claim latency metrics
- Service Bus ingress / egress spans and metrics
- De-duplication cache hit/miss counters
- Optional span events for validation / archive stages
- Configurable sampling (probabilistic/parent-based) via Telemetry options

---

Contributions welcome—feel free to open issues or draft PRs as the architecture evolves.

---

## Pipeline Roles (New Split Architecture)

The runtime now supports explicit role selection separating file discovery (polling) from event processing.

Roles are configured via `Pipeline:Role` (or environment variable `Pipeline__Role`).

Available values:

| Role   | Hosted Services Started | Typical Use Case                      |
| ------ | ----------------------- | ------------------------------------- |
| All    | Polling + Processing    | Local dev / simple single-node deploy |
| Poller | Polling only            | Dedicated ingestion node              |
| Worker | Processing only         | Horizontal scale-out workers          |

Example environment overrides:

```
Pipeline__Role=Poller

Pipeline__Role=Worker
```

`Pipeline__Role` fully determines polling vs processing. The only remaining feature flag in this area is `Features__EnableFileTransfer` which toggles actual file copy/move side effects.
