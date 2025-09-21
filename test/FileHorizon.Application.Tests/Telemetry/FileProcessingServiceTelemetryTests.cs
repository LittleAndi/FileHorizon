using System.Diagnostics;
using System.Diagnostics.Metrics;
using FileHorizon.Application.Core;
using FileHorizon.Application.Models;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Common.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests.Telemetry;

public sealed class FileProcessingServiceTelemetryTests
{
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
        var svc = new FileProcessingService(new SuccessProcessor(), NullLogger<FileProcessingService>.Instance);
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == TelemetryInstrumentation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { }
        };
        ActivitySource.AddActivityListener(listener);

        var initial = CollectCounterValue(TelemetryInstrumentation.FilesProcessed);
        await svc.HandleAsync(CreateEvent(), CancellationToken.None);
        var after = CollectCounterValue(TelemetryInstrumentation.FilesProcessed);
        Assert.Equal(initial + 1, after);
    }

    [Fact]
    public async Task Failure_Increments_FilesFailed()
    {
        var svc = new FileProcessingService(new FailureProcessor(), NullLogger<FileProcessingService>.Instance);
        var initial = CollectCounterValue(TelemetryInstrumentation.FilesFailed);
        await svc.HandleAsync(CreateEvent("fail"), CancellationToken.None);
        var after = CollectCounterValue(TelemetryInstrumentation.FilesFailed);
        Assert.Equal(initial + 1, after);
    }

    private static long CollectCounterValue(Counter<long> counter)
    {
        // Use an ad-hoc MeterListener to read current cumulative value.
        long value = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument == counter) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((inst, measurement, tags, state) =>
        {
            value += measurement;
        });
        listener.Start();
        // Record a zero to trigger publish path
        counter.Add(0);
        listener.Dispose();
        return value;
    }
}
