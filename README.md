# FileHorizon

[![CI](https://github.com/LittleAndi/FileHorizon/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/LittleAndi/FileHorizon/actions/workflows/ci.yml)

> Disclaimer: A significant portion of this project's code is intentionally authored with AI assistance (pair‑programming style) via pull requests that still pass through normal version control, code review, and CI quality gates. All generated contributions are curated, adjusted, and ultimately owned by the repository maintainer. If you spot something that can be improved, please open an issue or PR.

**FileHorizon** is an open-source, container-ready file transfer and orchestration system. Designed as a modern alternative to heavyweight integration platforms, it provides a lightweight yet reliable way to move files across **UNC paths, FTP, and SFTP** while ensuring observability and control. By leveraging **Redis** for distributed coordination, FileHorizon can scale out to multiple parallel containers without duplicate processing, making it suitable for both on-premises and hybrid cloud deployments.

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

### Configuration

Runtime configuration is provided via `appsettings.json` / environment variables. To override via environment variables, use the standard ASP.NET Core naming pattern, e.g.:

```
docker run --rm -p 8080:8080 \
	-e "Features__UseSyntheticPoller=false" \
	-e "Features__EnableFileTransfer=true" \
	filehorizon:dev
```

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
- `Features__EnablePolling` master switch for any polling (synthetic or directory). Set `false` on pure worker replicas.
- `Features__EnableProcessing` master switch for processing/dequeuing work; set `false` if you want a poller-only instance that just enqueues (rare) or for diagnostic dry runs.

You can override any value using an `.env` file placed next to `docker-compose.yml`:

```
# .env example
FEATURES__USESYNTHETICPOLLER=false
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
Features__EnablePolling=true
Features__EnableProcessing=false
Features__EnableFileTransfer=false

# worker (process only)
Features__EnablePolling=false
Features__EnableProcessing=true
Features__EnableFileTransfer=true
```

Alternatively (common simpler pattern):

```
# single poller that also processes
Features__EnablePolling=true
Features__EnableProcessing=true
Features__EnableFileTransfer=true

# additional workers (no polling)
Features__EnablePolling=false
Features__EnableProcessing=true
Features__EnableFileTransfer=true
```

> Reminder (WSL + containerd): If you previously built with `docker build`, rebuild with `nerdctl build` to ensure the image exists in the containerd image store before scaling.

### Future Hardening Ideas

- Switch to distroless or `-alpine` base (after validating native dependencies).
- Add read-only root filesystem (`--read-only`) with tmpfs mounts for transient storage.
- Introduce health/liveness/readiness probes in orchestration environments.

---

## Roadmap (Excerpt)

- Pending message claiming / retry logic for Redis Streams
- Idempotency and deduplication store
- Service Bus ingress/egress integration
- OpenTelemetry metrics/traces instrumentation
- SFTP/FTP protocol plugins

---

Contributions welcome—feel free to open issues or draft PRs as the architecture evolves.
