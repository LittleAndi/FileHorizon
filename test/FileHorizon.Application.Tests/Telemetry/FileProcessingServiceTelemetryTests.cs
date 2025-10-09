using System.Diagnostics;
using System.Diagnostics.Metrics;
using FileHorizon.Application.Core;
using FileHorizon.Application.Models;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Common.Telemetry; // still needed for activity source name
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests.Telemetry;

public sealed class FileProcessingServiceTelemetryTests
{
    private sealed class FakeTelemetry : IFileProcessingTelemetry
    {
        public int SuccessCount { get; private set; }
        public int FailureCount { get; private set; }
        public List<double> DurationsMs { get; } = new();

        public void RecordSuccess(string protocol, double elapsedMs)
        {
            SuccessCount++;
            DurationsMs.Add(elapsedMs);
        }

        public void RecordFailure(string protocol, double elapsedMs)
        {
            FailureCount++;
            DurationsMs.Add(elapsedMs);
        }
        public void RecordNotificationSuccess(double elapsedMs) { }
        public void RecordNotificationFailure(string reason) { }
        public void RecordNotificationSuppressed() { }
    }
    private sealed class SuccessProcessor : IFileProcessor
    {
        public Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private sealed class FailureProcessor : IFileProcessor
    {
        public Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct) => Task.FromResult(Result.Failure(Error.Unspecified("Test", "Failure")));
    }

    private static FileEvent CreateEvent(string id = "f1", long size = 10) =>
        new(id,
            new FileMetadata($"/tmp/{id}.dat", size, DateTimeOffset.UtcNow, "", null),
            DateTimeOffset.UtcNow,
            "local",
            $"/out/{id}.dat");

    [Fact]
    public async Task Success_Increments_FilesProcessed_And_Records_Activity()
    {
        var telemetry = new FakeTelemetry();
        var svc = new FileProcessingService(new SuccessProcessor(), NullLogger<FileProcessingService>.Instance, telemetry);

        // Activity listener (kept to ensure activity is created; not asserted yet but may be extended later)
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == TelemetryInstrumentation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { }
        };
        ActivitySource.AddActivityListener(activityListener);

        await svc.HandleAsync(CreateEvent(), CancellationToken.None);
        Assert.Equal(1, telemetry.SuccessCount);
        Assert.DoesNotContain(telemetry.DurationsMs, d => d <= 0);
    }

    [Fact]
    public async Task Failure_Increments_FilesFailed()
    {
        var telemetry = new FakeTelemetry();
        var svc = new FileProcessingService(new FailureProcessor(), NullLogger<FileProcessingService>.Instance, telemetry);
        await svc.HandleAsync(CreateEvent("fail"), CancellationToken.None);
        Assert.Equal(1, telemetry.FailureCount);
        Assert.DoesNotContain(telemetry.DurationsMs, d => d <= 0);
    }
}
