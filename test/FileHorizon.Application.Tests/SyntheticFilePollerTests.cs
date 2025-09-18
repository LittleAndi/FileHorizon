using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Infrastructure.Queue;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Tests;

public class SyntheticFilePollerTests
{
    private sealed class CapturingQueue : IFileEventQueue
    {
        public List<FileEvent> Items { get; } = new();
        public Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct)
        {
            Items.Add(fileEvent);
            return Task.FromResult(Result.Success());
        }
        public async IAsyncEnumerable<FileEvent> DequeueAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var i in Items)
            {
                yield return i;
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task PollAsync_Should_Enqueue_Exactly_One_Event()
    {
        var queue = new CapturingQueue();
        IFilePoller poller = new SyntheticFilePoller(queue);

        var result = await poller.PollAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(queue.Items);
        var fe = queue.Items[0];
        Assert.Equal("synthetic", fe.Protocol);
        Assert.True(fe.SourcePathMatchesDestination());
    }
}

internal static class FileEventTestExtensions
{
    public static bool SourcePathMatchesDestination(this FileEvent fe) => fe.DestinationPath == fe.Metadata.SourcePath;
}
