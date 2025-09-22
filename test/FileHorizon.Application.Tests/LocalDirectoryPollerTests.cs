using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Models;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace FileHorizon.Application.Tests;

public class LocalDirectoryPollerTests
{

    private sealed class TestQueue : IFileEventQueue
    {
        private readonly Channel<FileEvent> _channel = Channel.CreateUnbounded<FileEvent>();
        public Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct)
        {
            _channel.Writer.TryWrite(fileEvent);
            return Task.FromResult(Result.Success());
        }
        public async IAsyncEnumerable<FileEvent> DequeueAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await _channel.Reader.WaitToReadAsync(ct)) yield break;
                while (_channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        public IReadOnlyCollection<FileEvent> TryDrain(int maxCount)
        {
            if (maxCount <= 0) return Array.Empty<FileEvent>();
            var list = new List<FileEvent>();
            while (list.Count < maxCount && _channel.Reader.TryRead(out var item))
            {
                list.Add(item);
            }
            return list;
        }
    }

    [Fact]
    public async Task PollAsync_EnqueuesFileEvent_ForExistingFile()
    {
        // Arrange
        var tempRoot = Path.Combine(Path.GetTempPath(), "fh-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var filePath = Path.Combine(tempRoot, "sample.txt");
            await File.WriteAllTextAsync(filePath, "hello world");
            // Ensure mtime older than stability window
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(-5));

            var sourcesOptions = new FileSourcesOptions
            {
                Sources =
                [
                    new() { Name = "test", Path = tempRoot, Pattern = "*.txt", Recursive = false, MinStableSeconds = 1 }
                ]
            };
            var monitor = new OptionsMonitorStub<FileSourcesOptions>(sourcesOptions);
            var queue = new TestQueue();
            var logger = NullLogger<LocalDirectoryPoller>.Instance;
            var poller = new LocalDirectoryPoller(queue, logger, monitor);

            // Act
            var result = await poller.PollAsync(CancellationToken.None);

            // Assert
            Assert.True(result.IsSuccess);
            FileEvent? first = null;
            await foreach (var ev in queue.DequeueAsync(CancellationToken.None))
            {
                first = ev;
                break;
            }
            Assert.NotNull(first);
            // Local identity now normalized to forward-slash absolute path starting with '/'
            var normalized = filePath.Replace("\\", "/");
            if (!normalized.StartsWith('/')) normalized = "/" + normalized;
            Assert.Equal(normalized, first!.Metadata.SourcePath);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }
}
