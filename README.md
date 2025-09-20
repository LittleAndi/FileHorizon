# FileHorizon

[![CI](https://github.com/LittleAndi/FileHorizon/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/LittleAndi/FileHorizon/actions/workflows/ci.yml)

> Disclaimer: A significant portion of this project's code is intentionally authored with AI assistance (pair‑programming style) via pull requests that still pass through normal version control, code review, and CI quality gates. All generated contributions are curated, adjusted, and ultimately owned by the repository maintainer. If you spot something that can be improved, please open an issue or PR.

**FileHorizon** is an open-source, container-ready file transfer and orchestration system. Designed as a modern alternative to heavyweight integration platforms, it provides a lightweight yet reliable way to move files across **UNC paths, FTP, and SFTP** while ensuring observability and control. By leveraging **Redis** for distributed coordination, FileHorizon can scale out to multiple parallel containers without duplicate processing, making it suitable for both on-premises and hybrid cloud deployments.

Configuration is centralized through **Azure App Configuration** and **Azure Key Vault**, enabling secure, dynamic management of connections and destinations. With **OpenTelemetry** at its core, FileHorizon delivers unified **logging, metrics, and tracing** out of the box—no separate logging stack required. The system emphasizes **safety and consistency**, ensuring files are only picked up once they are fully written at the source.

FileHorizon is built for teams that need the reliability of managed file transfer (MFT) but want the flexibility, transparency, and scalability of modern open-source tooling.

## Container Image

This repository includes a multi-stage `Dockerfile` for building a lean runtime image that runs as a non-root user.

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

### Future Hardening Ideas

- Switch to distroless or `-alpine` base (after validating native dependencies). 
- Add read-only root filesystem (`--read-only`) with tmpfs mounts for transient storage. 
- Introduce health/liveness/readiness probes in orchestration environments.

