using System.Threading.Channels;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Infrastructure.Queue;

/// <summary>
/// In-memory implementation of <see cref="IFileEventQueue"/> backed by an unbounded Channel.
/// This is suitable for early development and tests only.
/// </summary>
public sealed class InMemoryFileEventQueue : IFileEventQueue
{
    private readonly Channel<FileEvent> _channel;
    private readonly ILogger<InMemoryFileEventQueue> _logger;

    public InMemoryFileEventQueue(ILogger<InMemoryFileEventQueue> logger)
    {
        _logger = logger;
        var options = new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false
        };
        _channel = Channel.CreateUnbounded<FileEvent>(options);
        _logger.LogInformation("InMemoryFileEventQueue initialized");
    }

    public Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Enqueue cancelled for file {FileId}", fileEvent.Id);
            return Task.FromResult(Result.Failure(Error.Unspecified("Queue.EnqueueCancelled", "Enqueue was cancelled")));
        }

        if (!_channel.Writer.TryWrite(fileEvent))
        {
            _logger.LogWarning("Failed to enqueue file event {FileId} - queue full/rejected", fileEvent.Id);
            return Task.FromResult(Result.Failure(Error.Unspecified("Queue.Full", "Queue rejected the item")));
        }
        _logger.LogDebug("Enqueued file event {FileId}", fileEvent.Id);
        return Task.FromResult(Result.Success());
    }

    public async IAsyncEnumerable<FileEvent> DequeueAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // We avoid yielding within a try/catch (language restriction) and instead cooperatively check cancellation.
        while (!ct.IsCancellationRequested)
        {
            // WaitToReadAsync without passing the external CT; we handle cancellation explicitly to avoid exceptions.
            var ready = await _channel.Reader.WaitToReadAsync().ConfigureAwait(false);
            if (!ready)
            {
                await Task.Delay(10, ct).ConfigureAwait(false); // brief backoff
                continue;
            }
            while (_channel.Reader.TryRead(out var item))
            {
                _logger.LogDebug("Dequeued file event {FileId}", item.Id);
                yield return item;
                if (ct.IsCancellationRequested) yield break;
            }
        }
    }
}
