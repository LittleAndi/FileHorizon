Based on the current FileHorizon implementation, this document marks which metrics/telemetry are already provided by the backend and where the UI can safely visualize them. Legend: ✅ covered • 🟨 partially covered/derivable • ❌ not covered (needs backend or external source).

## **📊 Core System Metrics**

### **1. Active Transfers** 🟨

- What we have: Traces for in-flight work (spans: `file.process`, `file.orchestrate`, `reader.open`, `sink.write`).
- UI can derive “active now” from currently-open spans. There is no direct gauge metric yet.
- Rewrite suggestion: “Active transfers (derived from active spans)”.

### **2. Queue Depth** 🟨

- What we have: Counters `queue.enqueued`, `queue.dequeued`, `queue.enqueue.failures`, `queue.dequeue.failures` and spans `queue.enqueue`, `queue.dequeue`.
- Depth itself (stream length) isn’t emitted yet; can be added as a gauge. Trends are derivable from counters in the meantime.
- Rewrite suggestion: “Queue activity (enqueued/dequeued rate); add stream length gauge for depth.”

### **3. Success Rate** ✅

- What we have: Counters `files.processed`, `files.failed` (protocol-tagged). UI can compute percentage over a 24h window.
- `files.processed` counts files transferred to a destination. Files the orchestrator skips (already transferred per the idempotency store, or no destination plan) are not counted, so re-enqueued backlog after a restart does not show up as successes. `processing.duration.ms` likewise excludes skips. Idempotent skips are counted in `files.skipped.idempotent` instead.

### **4. System Load** ❌

- Not emitted by the app. Use container/node telemetry (e.g., cAdvisor/Node Exporter/Prometheus).

---

## **🔌 Protocol-Specific Metrics**

### **5. UNC Sources** 🟨

- What we have: Local polling emits discoveries via `files.discovered`; unstable files via `files.skipped.unstable`; failures via `poll.source.errors` (shared with remote).
- Online/offline source counts aren’t tracked; a per-source health gauge would complete this.

### **6. FTP/SFTP Servers** 🟨

- What we have: Remote poll spans (`poll.remote.cycle`, `poll.remote.source`) and `poll.source.errors` counter. Discovery flows increment `files.discovered`.
- Connected vs total would benefit from a connect_success/connect_failure metric per source (tagged by source name).

### **7. Azure Service Bus** ❌

- Not applicable in current codebase.

---

## **📁 File Transfer Details (Table)**

### **8. Per-Transfer Metrics** ✅ (with notes)

- Covered fields from backend:
  - Transaction ID → `FileEvent.Id`
  - Filename/Source path → `FileEvent.Metadata.SourcePath`
  - Destination path → `FileEvent.DestinationPath`
  - Protocol → `FileEvent.Protocol`
  - File size → `FileEvent.Metadata.SizeBytes`
  - Start time → `FileEvent.DiscoveredAtUtc` (plus span start times)
  - Duration → `processing.duration.ms` histogram and span durations
- Status mapping:
  - processing/completed/error/queued can be inferred from spans and `files.processed`/`files.failed` counters.
- Progress percentage:
  - Not emitted; would require per-file progress or periodic write metrics. Could estimate if we add per-file bytes copied and total size.

---

## **⚡ Performance Metrics**

### **9. Throughput** ✅

- What we have: `bytes.copied` counter (sink-level) and `processing.duration.ms` histogram. Throughput can be computed over time windows.
- Enhancement: tag `bytes.copied` by `sink.name` and `file.protocol` for breakdowns.

### **10. Container Memory** ❌

- Not emitted by the app. Use container runtime metrics.

### **11. Redis Streams Health** 🟨

- What we have: `queue.enqueue.failures`, `queue.dequeue.failures` counters, and queue spans. Connection quality and lag gauges not yet emitted.
- Rewrite suggestion: “Queue health (enqueue/dequeue failures and activity rate)”. Add a simple health/latency gauge for richer status.

---

## **📝 Recent Events (Activity Log)**

### **12. Event Stream** ✅

- Success: green
- Error: red (pulsing)
- Info: blue
- What we have: Rich traces (OTLP) for processing and polling stages, plus logs. UI can map span status to visual indicators.

---

## **📈 Additional Context Metrics**

These are implied or supporting metrics that enhance the dashboard:

13. **Trend indicators** ✅ (derive from counters/histograms over time windows)
14. **Status badges** ✅ (drive from thresholds on processed/failed, poll errors, queue failures)
15. **Time-based filtering** ✅ (metrics are time-series; use 24h windows in dashboards)
16. **Protocol distribution** ✅ (protocol-tagged counters/histograms)
17. **Error rate tracking** ✅ (from `files.failed` vs `files.processed`)
18. **Real-time vs historical** ✅ (metrics + traces support both views)

---

## **🎯 Summary**

The dashboard tracks **11 primary metric categories** covering:

- System health (4 core metrics)
- Protocol connectivity (3 protocol groups)
- Individual transfer tracking (detailed table)
- Performance monitoring (3 infrastructure metrics)
- Event logging (activity stream)

All metrics support real-time updates, trend analysis, and visual status indicators to give operators comprehensive visibility into the FileHorizon MFT system.

---

## 📦 Current instrumentation inventory (for dashboard bindings)

- Counters
  - `files.processed` (transfers only; skipped files excluded), `files.failed`
  - `bytes.copied`
  - `queue.enqueued`, `queue.dequeued`, `queue.enqueue.failures`, `queue.dequeue.failures`
  - `poll.cycles`, `poll.source.errors`, `files.discovered`, `files.skipped.unstable`, `files.skipped.idempotent`
- Histograms
  - `processing.duration.ms` (transfers and failures; skipped files excluded), `poll.cycle.duration.ms`
- Spans (ActivitySource: `FileHorizon.Pipeline`)
  - `pipeline.lifetime`
  - `file.process` (FileProcessingService)
  - `file.orchestrate` (Orchestrator)
  - `reader.open`, `sink.write`
  - `poll.remote.cycle`, `poll.remote.source`
  - `queue.enqueue`, `queue.dequeue`

## 🔧 Suggested backend extensions (optional)

- Active transfers gauge: observable that tracks in-flight transfers (inc on start, dec on finish).
- Queue depth gauge: report Redis Stream length(s) for precise backlog.
- Source connectivity: counters/gauges for connect_success/connect_failure per source (tagged by source name).
- Throughput breakdowns: tag `bytes.copied` with `sink.name` and `file.protocol` for richer PromQL slices.

---

## 🧩 Optional next steps (implementation checklist)

These items are not required for the current dashboard but can be added incrementally to enrich insights:

- [ ] Active transfers gauge
  - Backend: Add an observable up/down counter or gauge that increments on `file.orchestrate` start and decrements on completion/error.
  - UI: Show “Active now” count and sparkline.
- [ ] Queue depth gauge
  - Backend: Periodically emit Redis Stream length per queue as a gauge (tags: queue name, consumer group).
  - UI: Display current backlog and alert on thresholds.
- [ ] Source connectivity metrics
  - Backend: Emit `connect_success` / `connect_failure` counters per source (tag: source name, protocol); optionally a last-success timestamp gauge.
  - UI: Show connected vs total sources and recent failures.
- [ ] Throughput breakdown tags
  - Backend: Tag `bytes.copied` with `sink.name` and `file.protocol` for per-sink/protocol slices.
  - UI: Add stacked area/bar charts by sink/protocol.

Note: Each enhancement should include minimal unit tests (instrument existence and basic tag shape) and can be rolled out independently.
