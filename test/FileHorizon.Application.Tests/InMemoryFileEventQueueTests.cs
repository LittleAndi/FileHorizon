using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Infrastructure.Queue;
using FileHorizon.Application.Models;
using FileHorizon.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;
using FileHorizon.Application.Validation;

namespace FileHorizon.Application.Tests;

public class InMemoryFileEventQueueTests
{
    private static FileEvent NewEvent(string name) => new(
        Id: Guid.NewGuid().ToString(),
        Metadata: new FileMetadata($"/tmp/{name}.dat", 10, DateTimeOffset.UtcNow, "sha256", null),
        DiscoveredAtUtc: DateTimeOffset.UtcNow,
        Protocol: "local",
        DestinationPath: $"/dest/{name}.dat");

    [Fact]
    public async Task Enqueue_Then_Dequeue_Single_Item()
    {
        var validator = new BasicFileEventValidator();
        IFileEventQueue queue = new InMemoryFileEventQueue(NullLogger<InMemoryFileEventQueue>.Instance, validator);
        var ev = NewEvent("one");
        var r = await queue.EnqueueAsync(ev, CancellationToken.None);
        Assert.True(r.IsSuccess);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await foreach (var item in queue.DequeueAsync(cts.Token))
        {
            Assert.Equal(ev, item);
            break; // only need first
        }
    }

    [Fact]
    public async Task Enqueue_Multiple_Preserves_Order()
    {
        var validator = new BasicFileEventValidator();
        IFileEventQueue queue = new InMemoryFileEventQueue(NullLogger<InMemoryFileEventQueue>.Instance, validator);
        var ev1 = NewEvent("one");
        var ev2 = NewEvent("two");
        await queue.EnqueueAsync(ev1, CancellationToken.None);
        await queue.EnqueueAsync(ev2, CancellationToken.None);

        var list = new List<FileEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await foreach (var item in queue.DequeueAsync(cts.Token))
        {
            list.Add(item);
            if (list.Count == 2) break;
        }
        Assert.Equal(new[] { ev1, ev2 }, list);
    }

    [Fact]
    public async Task Cancellation_Stops_Dequeue()
    {
        var validator = new BasicFileEventValidator();
        IFileEventQueue queue = new InMemoryFileEventQueue(NullLogger<InMemoryFileEventQueue>.Instance, validator);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var enumerated = false;
        await foreach (var _ in queue.DequeueAsync(cts.Token))
        {
            enumerated = true;
        }
        Assert.False(enumerated);
    }

    [Fact]
    public async Task Enqueue_Invalid_Event_Should_Return_Failure()
    {
        var validator = new BasicFileEventValidator();
        IFileEventQueue queue = new InMemoryFileEventQueue(NullLogger<InMemoryFileEventQueue>.Instance, validator);
        var invalid = new FileEvent("", new FileMetadata("", -1, DateTimeOffset.UtcNow, "sha256", null), DateTimeOffset.UtcNow, "local", "");
        var r = await queue.EnqueueAsync(invalid, CancellationToken.None);
        Assert.True(r.IsFailure);
        Assert.Equal(Error.Validation.EmptyId.Code, r.Error.Code);
    }
}
