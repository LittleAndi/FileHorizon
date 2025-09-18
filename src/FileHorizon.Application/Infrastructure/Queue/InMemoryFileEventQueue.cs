using System.Threading.Channels;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Infrastructure.Queue;

/// <summary>
/// In-memory implementation of <see cref="IFileEventQueue"/> backed by an unbounded Channel.
/// This is suitable for early development and tests only.
/// </summary>
public sealed class InMemoryFileEventQueue : IFileEventQueue
{
    private readonly Channel<FileEvent> _channel;

    public InMemoryFileEventQueue()
    {
        var options = new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false
        };
        _channel = Channel.CreateUnbounded<FileEvent>(options);
    }

    public Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(Result.Failure(Error.Unspecified("Queue.EnqueueCancelled", "Enqueue was cancelled")));
        }

        if (!_channel.Writer.TryWrite(fileEvent))
        {
            return Task.FromResult(Result.Failure(Error.Unspecified("Queue.Full", "Queue rejected the item")));
        }
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
                yield return item;
                if (ct.IsCancellationRequested) yield break;
            }
        }
    }
}
