using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Core;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public class FileProcessingServiceTests
{
    private sealed class FakeTelemetry : IFileProcessingTelemetry
    {
        public int Success; public int Failure; public List<double> Durations = new();
        public void RecordSuccess(string protocol, double elapsedMs) { Success++; Durations.Add(elapsedMs); }
        public void RecordFailure(string protocol, double elapsedMs) { Failure++; Durations.Add(elapsedMs); }
    }
    private sealed class TestFileProcessor : IFileProcessor
    {
        private readonly Func<FileEvent, CancellationToken, Task<Result>> _impl;
        public int CallCount { get; private set; }
        public TestFileProcessor(Func<FileEvent, CancellationToken, Task<Result>> impl)
        {
            _impl = impl;
        }
        public Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct)
        {
            CallCount++;
            return _impl(fileEvent, ct);
        }
    }

    [Fact]
    public async Task HandleAsync_Should_Delegate_And_Return_Result()
    {
        var fileEvent = new FileEvent(
            Id: Guid.NewGuid().ToString(),
            Metadata: new FileMetadata("/tmp/a.txt", 123, DateTimeOffset.UtcNow, "sha256", null),
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: "local",
            DestinationPath: "/dest/a.txt",
            DeleteAfterTransfer: false);

        var expected = Result.Success();
        var testProcessor = new TestFileProcessor((fe, ct) => Task.FromResult(expected));
        var telemetry = new FakeTelemetry();
        var svc = new FileProcessingService(testProcessor, NullLogger<FileProcessingService>.Instance, telemetry);

        var result = await svc.HandleAsync(fileEvent, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, testProcessor.CallCount);
        Assert.Equal(1, telemetry.Success);
    }
}
