using System.Threading.Channels;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Models;
using FileHorizon.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FileHorizon.Application.Tests;

public class LocalDirectoryPollerTests
{
    private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T>
    {
        public OptionsMonitorStub(T value) => CurrentValue = value;
        public T CurrentValue { get; set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

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
                Sources = new List<FileSourceOptions>
                {
                    new() { Name = "test", Path = tempRoot, Pattern = "*.txt", Recursive = false, MinStableSeconds = 1 }
                }
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
            Assert.Equal(filePath, first!.Metadata.SourcePath);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }
}
