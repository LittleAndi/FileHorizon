using FileHorizon.Application.Common.Telemetry;

namespace FileHorizon.Application.Tests.Telemetry;

public sealed class MetricsTests
{
    [Fact]
    public void Telemetry_Instruments_Available()
    {
        // Ensure meters and counters exist and have expected names
        Assert.Equal("FileHorizon", TelemetryInstrumentation.Meter.Name);
        Assert.Equal("files.processed", TelemetryInstrumentation.FilesProcessed.Name);
        Assert.Equal("files.failed", TelemetryInstrumentation.FilesFailed.Name);
        Assert.Equal("bytes.copied", TelemetryInstrumentation.BytesCopied.Name);
    }
}
