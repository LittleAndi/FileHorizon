using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common.Telemetry;
using System.Collections.Generic;

namespace FileHorizon.Application.Infrastructure.Telemetry;

internal sealed class FileProcessingTelemetry : IFileProcessingTelemetry
{
    public void RecordSuccess(string protocol, double elapsedMs)
    {
        TelemetryInstrumentation.ProcessingDurationMs.Record(elapsedMs, KeyValuePair.Create<string, object?>("file.protocol", protocol));
        TelemetryInstrumentation.FilesProcessed.Add(1, KeyValuePair.Create<string, object?>("file.protocol", protocol));
    }

    public void RecordFailure(string protocol, double elapsedMs)
    {
        TelemetryInstrumentation.ProcessingDurationMs.Record(elapsedMs, KeyValuePair.Create<string, object?>("file.protocol", protocol));
        TelemetryInstrumentation.FilesFailed.Add(1, KeyValuePair.Create<string, object?>("file.protocol", protocol));
    }
}
