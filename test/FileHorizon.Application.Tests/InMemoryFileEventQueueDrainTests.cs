using System.Threading.Channels;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Infrastructure.Queue;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileHorizon.Application.Tests;

public class InMemoryFileEventQueueDrainTests
{
    private sealed class PassThroughValidator : IFileEventValidator
    {
        public Result Validate(FileEvent fileEvent) => Result.Success();
    }

    [Fact]
    public async Task TryDrain_ReturnsEmpty_WhenQueueEmpty()
    {
        var q = new InMemoryFileEventQueue(NullLogger<InMemoryFileEventQueue>.Instance, new PassThroughValidator());
        var drained = q.TryDrain(10);
        Assert.Empty(drained);
        // ensure we can still enqueue & drain
        var fe = new FileEvent("id", new FileMetadata("/tmp/a.txt", 1, DateTimeOffset.UtcNow.AddMinutes(-1), "none", null), DateTimeOffset.UtcNow, "test", "/tmp/a.txt", false);
        await q.EnqueueAsync(fe, CancellationToken.None);
        var drained2 = q.TryDrain(5);
        Assert.Single(drained2);
        Assert.Equal("id", drained2.First().Id);
    }
}
