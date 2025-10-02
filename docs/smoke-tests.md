# Smoke Tests

This guide helps you quickly verify the stack locally using Docker Compose. By default, only the Local poller is enabled, and FTP/SFTP pollers are disabled.

Prerequisites

- Docker Desktop installed and running
- Ports available locally: 8080 (app), 9090 (Prometheus), 3000 (Grafana), 2222 (SFTP)

Stack bring-up

- Start the stack in the background
- Wait for the poller health endpoint to report healthy (the worker runs too, but only the poller exposes its health at 8080)

Quick checks

- App health: open http://localhost:8080/health in your browser; you should see "Healthy"
- Prometheus: open http://localhost:9090
- Grafana: open http://localhost:3000 (admin/admin by default)

Local poller smoke

- Place a sample file into the local inbox
  - Source folder on the host: `_data/inboxA`
  - Expected destination: `_data/outboxA`
- Confirm the file appears in `_data/outboxA` shortly after; check poller logs if needed

SFTP service (optional, discovery only for now)

- The compose file includes an SFTP server container based on `atmoz/sftp`
  - Host: localhost
  - Port: 2222
  - Username: demo
  - Password: password
  - Upload directory: `/upload` (mapped to host `_data/sftp-in`)
- IMPORTANT: SFTP poller is disabled by default. You can enable it by setting `Features__EnableSftpPoller=true` on the `poller` service and restarting that service. However, end-to-end file transfer from SFTP to local destinations is not implemented yet—the current processor only supports local filesystem moves. With SFTP polling enabled, you can still verify that files are discovered (via logs/metrics), but transfers will not complete until the new orchestrated processor lands.

Observability pointers

- Metrics (Prometheus): scrape target comes from the app; check counters for poll cycles, files discovered, and errors
- Dashboards (Grafana): basic provisioning is included; expand as the orchestrator and adapters add metrics

Notes

- If any container fails to become healthy, restart the stack after a short pause
- For large-volume tests, prefer placing multiple files into `_data/inboxA` and observe throughput and backoff behavior in the logs

## Orchestrated processor (default)

The orchestrated processor routes files using `Routing` rules and writes to configured `Destinations`. It is enabled by default.

Configure minimal Routing and Destinations via environment variables (examples):

    - `Routing__Rules__0__Name=local-all`
    - `Routing__Rules__0__Protocol=local`
    - `Routing__Rules__0__PathGlob=**/*.*`
    - `Routing__Rules__0__Destinations__0=OutboxA`
    - `Routing__Rules__0__Overwrite=true`
    - `Destinations__Local__0__Name=OutboxA`
    - `Destinations__Local__0__RootPath=./_data/outboxA`

Or appsettings snippet (Development):

    ```json
    {

// no feature flag required; orchestrator is default
"Routing": {
"Rules": [
{
"Name": "local-all",
"Protocol": "local",
"PathGlob": "\*_/_.\*",
"Destinations": ["OutboxA"],
"Overwrite": true
}
]
},
"Destinations": {
"Local": [{ "Name": "OutboxA", "RootPath": "./_data/outboxA" }]
}
}
```

Verify end-to-end

- With orchestrator enabled and rules configured:
  - Place a file in `_data/inboxA` (e.g., `_data/inboxA/sample.txt`).
  - Within a moment, confirm the file appears in `_data/outboxA`.
  - Check logs for a `file.process` span and `file.orchestrate` activity; metrics counters will increment.

Notes

- Orchestrator currently uses the first matching destination only. Multi-destination writes will arrive later.
- Paths in rules/globs are OS-dependent; Windows paths are normalized internally for glob matching.
