using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FileHorizon.Application.Common.Telemetry;

public static class TelemetryInstrumentation
{
    public const string ActivitySourceName = "FileHorizon.Pipeline";
    public const string MeterName = "FileHorizon";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName, version: "1.0.0");

    // Counters
    public static readonly Counter<long> FilesProcessed = Meter.CreateCounter<long>("files.processed", description: "Number of files successfully processed");
    public static readonly Counter<long> FilesFailed = Meter.CreateCounter<long>("files.failed", description: "Number of files that failed processing");
    public static readonly Counter<long> QueueEnqueued = Meter.CreateCounter<long>("queue.enqueued", description: "Number of file events enqueued");
    public static readonly Counter<long> QueueDequeued = Meter.CreateCounter<long>("queue.dequeued", description: "Number of file events dequeued");
    public static readonly Counter<long> QueueEnqueueFailures = Meter.CreateCounter<long>("queue.enqueue.failures", description: "Number of failed enqueue attempts");
    public static readonly Counter<long> QueueDequeueFailures = Meter.CreateCounter<long>("queue.dequeue.failures", description: "Number of dequeue attempts that resulted in errors");

    // Histograms
    public static readonly Histogram<double> ProcessingDurationMs = Meter.CreateHistogram<double>("processing.duration.ms", unit: "ms", description: "File processing duration in milliseconds");
}
