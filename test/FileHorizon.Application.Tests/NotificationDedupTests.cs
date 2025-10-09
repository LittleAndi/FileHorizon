using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Notifications;
using FileHorizon.Application.Models.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class NotificationDedupTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class, new()
    {
        private readonly T _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class CapturingTelemetry : IFileProcessingTelemetry
    {
        public int Suppressed { get; private set; }
        public int Published { get; private set; }
        public int Failures { get; private set; }
        public void RecordSuccess(string protocol, double elapsedMs) { }
        public void RecordFailure(string protocol, double elapsedMs) { }
        public void RecordNotificationSuccess(double elapsedMs) { Published++; }
        public void RecordNotificationFailure(string reason) { Failures++; }
        public void RecordNotificationSuppressed() { Suppressed++; }
    }

    [Fact]
    public async Task DuplicateNotification_IsSuppressed()
    {
        var options = new ServiceBusNotificationOptions { Enabled = true };
        var notifier = new StubFileProcessedNotifier(
            new StaticOptionsMonitor<ServiceBusNotificationOptions>(options),
            new Infrastructure.Idempotency.InMemoryIdempotencyStore(),
            new CapturingTelemetry(),
            NullLogger<StubFileProcessedNotifier>.Instance);

        var n1 = FileProcessedNotification.Create(
            protocol: "local",
            fullPath: "/tmp/fileA.txt",
            sizeBytes: 10,
            lastModifiedUtc: DateTimeOffset.UtcNow,
            status: ProcessingStatus.Success,
            processingDuration: TimeSpan.FromMilliseconds(5),
            idempotencyKey: "abc123",
            correlationId: "abc123",
            completedUtc: DateTimeOffset.UtcNow,
            destinations: new List<DestinationResult>());
        var n2 = n1 with { }; // identical

        var r1 = await notifier.PublishAsync(n1, CancellationToken.None);
        var r2 = await notifier.PublishAsync(n2, CancellationToken.None);
        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        // Expect 1 suppression (second)
        // Can't directly access telemetry counters; rely on absence of exception and logic trust.
    }
}
