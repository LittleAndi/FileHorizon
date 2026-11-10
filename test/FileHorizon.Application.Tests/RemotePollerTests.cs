using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Models;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FileHorizon.Application.Common.Telemetry;

namespace FileHorizon.Application.Tests;

public class RemotePollerTests
{
    private sealed record FakeRemoteFile(string FullPath, long Size, DateTimeOffset LastWrite, bool IsDir = false);

    private sealed class FakeRemoteFileInfo(string fullPath, long size, DateTimeOffset lastWrite, bool isDir) : IRemoteFileInfo
    {
        public string FullPath { get; } = fullPath; public string Name { get; } = System.IO.Path.GetFileName(fullPath);
        public long Size { get; } = size; public DateTimeOffset LastWriteTimeUtc { get; } = lastWrite; public bool IsDirectory { get; } = isDir;
    }

    private sealed class FakeRemoteClient : IRemoteFileClient
    {
        private readonly List<FakeRemoteFile> _files;
        private readonly bool _failConnect;
        private readonly Exception? _listFailure;
        private readonly Exception? _connectFailure;
        public FakeRemoteClient(string host, int port, IEnumerable<FakeRemoteFile> files, bool failConnect = false, Exception? listFailure = null, Exception? connectFailure = null)
        { Host = host; Port = port; _files = files.ToList(); _failConnect = failConnect; _listFailure = listFailure; _connectFailure = connectFailure; }
        public string Host { get; }
        public int Port { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task ConnectAsync(CancellationToken ct)
        {
            if (_connectFailure is not null) throw _connectFailure;
            if (_failConnect) throw new InvalidOperationException("connect fail");
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<IRemoteFileInfo> ListFilesAsync(string remotePath, bool recursive, string pattern, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var f in _files) { yield return new FakeRemoteFileInfo(f.FullPath, f.Size, f.LastWrite, f.IsDir); await Task.Yield(); }
            if (_listFailure is not null) throw _listFailure; // fails after the listed files, like a stalled READDIR batch
        }
        public IAsyncEnumerable<IRemoteFileInfo> ListTreeAsync(string remotePath, bool recursive, string pattern, CancellationToken ct)
            => ListFilesAsync(remotePath, recursive, pattern, ct); // the fake already yields directories
        public Task<IRemoteFileInfo?> GetFileInfoAsync(string path, CancellationToken ct) => Task.FromResult<IRemoteFileInfo?>(null); // not used
        public Task DeleteAsync(string fullPath, CancellationToken ct) => Task.CompletedTask; // deletion not exercised in these tests
    }

    private sealed class TestQueue : IFileEventQueue
    {
        public readonly ConcurrentQueue<FileEvent> Events = new();
        /// <summary>How many upcoming enqueues are rejected with a failed Result before the queue accepts again.</summary>
        public int RejectNext;
        /// <summary>Optional per-event rejection, checked before <see cref="RejectNext"/>.</summary>
        public Func<FileEvent, Error?>? RejectWith;
        /// <summary>Optional per-event exception, thrown instead of returning a result.</summary>
        public Func<FileEvent, Exception?>? ThrowFor;
        public Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct)
        {
            // Like the real queues, validation comes first and is permanent for that event.
            if (fileEvent.Metadata.SizeBytes < 0) return Task.FromResult(Result.Failure(Error.Validation.NegativeSize));
            if (ThrowFor?.Invoke(fileEvent) is { } exception) throw exception;
            if (RejectWith?.Invoke(fileEvent) is { } error) return Task.FromResult(Result.Failure(error));
            if (RejectNext > 0)
            {
                RejectNext--;
                return Task.FromResult(Result.Failure(Error.Transient("Queue.Full", "queue is full")));
            }
            Events.Enqueue(fileEvent); return Task.FromResult(Result.Success());
        }
        public IAsyncEnumerable<FileEvent> DequeueAsync(CancellationToken ct) => Empty();
        private static async IAsyncEnumerable<FileEvent> Empty()
        { await Task.CompletedTask; yield break; }
        public IReadOnlyCollection<FileEvent> TryDrain(int maxCount)
        { var list = new List<FileEvent>(); while (list.Count < maxCount && Events.TryDequeue(out var ev)) list.Add(ev); return list; }
    }

    private sealed class TestRemotePoller : RemotePollerBase
    {
        private readonly List<IRemoteFileSourceDescriptor> _sources;
        private readonly Func<object, IRemoteFileClient> _clientFactory;
        internal TestRemotePoller(IFileEventQueue queue, IOptionsMonitor<RemoteFileSourcesOptions> opts, Func<object, IRemoteFileClient> clientFactory, TimeProvider? time = null)
            : base(queue, opts, NullLogger<TestRemotePoller>.Instance, time)
        { _clientFactory = clientFactory; _sources = new(); foreach (var f in opts.CurrentValue.Ftp) if (f.Enabled) _sources.Add(new Src(f.Name, f.RemotePath, f.Pattern, f.Recursive, f.MinStableSeconds, f.DestinationPath, f.Host, f.Port)); }
        protected override List<IRemoteFileSourceDescriptor> GetEnabledSources() => _sources;
        protected override IRemoteFileClient CreateClient(IRemoteFileSourceDescriptor source) => _clientFactory(source);
        private sealed class Src(string name, string path, string pattern, bool rec, int stable, string? dest, string host, int port) : IRemoteFileSourceDescriptor
        {
            public string Name => name;
            public string RemotePath => path;
            public string Pattern => pattern;
            public bool Recursive => rec;
            public int MinStableSeconds => stable;
            public string? DestinationPath => dest;
            public string Host => host;
            public int Port => port;
            public bool DeleteAfterTransfer => false; // tests default: no deletion
            public ProtocolType Protocol => ProtocolType.Ftp;
        }
    }

    /// <summary>A clock that only moves when the test moves it, so backoff and pause windows are deterministic.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static OptionsMonitorStub<RemoteFileSourcesOptions> CreateOptions(params FtpSourceOptions[] ftp)
        => new(new RemoteFileSourcesOptions { Ftp = ftp.ToList(), Sftp = new() });

    [Fact]
    public async Task PollAsync_StableFile_EnqueuesOnce()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var file = new FakeRemoteFile("/a.txt", 5, DateTimeOffset.UtcNow.AddSeconds(-10));
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, new[] { file }));

        await poller.PollAsync(CancellationToken.None);
        await poller.PollAsync(CancellationToken.None); // second poll should not enqueue duplicate (observation snapshot updated)

        var events = queue.TryDrain(10);
        Assert.Single(events);
        Assert.Contains("ftp://", events.First().Metadata.SourcePath); // identity key
    }

    [Fact]
    public async Task PollAsync_UnstableFile_FirstSkippedThenEnqueued()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 2 });
        var queue = new TestQueue();
        var dynamicTime = DateTimeOffset.UtcNow; // will simulate size change then stability
        var filesSequence = new List<FakeRemoteFile[]> {
            new [] { new FakeRemoteFile("/b.txt", 10, dynamicTime) },
            new [] { new FakeRemoteFile("/b.txt", 10, dynamicTime) } // stable second time
        };
        int call = 0;
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, filesSequence[Math.Min(call++, filesSequence.Count - 1)]));

        await poller.PollAsync(CancellationToken.None); // first pass - not stable due to MinStableSeconds > 0 (timestamp now)
        await Task.Delay(2100); // allow stability window to elapse
        await poller.PollAsync(CancellationToken.None);

        var events = queue.TryDrain(10);
        Assert.Single(events);
        Assert.EndsWith("/b.txt", events.First().Metadata.SourcePath);
    }

    [Fact]
    public async Task PollAsync_ChangedFile_IsReDispatchedAsNewVersion()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var versions = new List<FakeRemoteFile[]> {
            new [] { new FakeRemoteFile("/a.txt", 5, mtime) },   // poll 1: dispatched
            new [] { new FakeRemoteFile("/a.txt", 9, mtime) },   // poll 2: size changed -> unstable, baseline reset
            new [] { new FakeRemoteFile("/a.txt", 9, mtime) },   // poll 3: stable again -> must re-dispatch
        };
        int call = 0;
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, versions[Math.Min(call++, versions.Count - 1)]));

        await poller.PollAsync(CancellationToken.None);
        await poller.PollAsync(CancellationToken.None);
        await poller.PollAsync(CancellationToken.None);

        var events = queue.TryDrain(10);
        Assert.Equal(2, events.Count); // original version + changed version
        Assert.All(events, e => Assert.EndsWith("/a.txt", e.Metadata.SourcePath));
    }

    [Fact]
    public async Task PollAsync_UnchangedFile_IsNotReDispatched()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var file = new FakeRemoteFile("/a.txt", 5, DateTimeOffset.UtcNow.AddMinutes(-10));
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, new[] { file }));

        await poller.PollAsync(CancellationToken.None);
        await poller.PollAsync(CancellationToken.None);
        await poller.PollAsync(CancellationToken.None);

        Assert.Single(queue.TryDrain(10));
    }

    [Fact]
    public async Task PollAsync_ConnectionFailure_TriggersBackoff()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        int attempts = 0;
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, Array.Empty<FakeRemoteFile>(), failConnect: true));

        await poller.PollAsync(CancellationToken.None); // failure -> backoff scheduled
        attempts++;
        // Immediately poll again; should skip due to backoff, keeping attempts == 1
        await poller.PollAsync(CancellationToken.None);
        // No events because connect always fails
        Assert.Empty(queue.TryDrain(5));
    }

    [Fact]
    public async Task PollAsync_ListingFailure_DoesNotStopOtherSources()
    {
        var opts = CreateOptions(
            new FtpSourceOptions { Name = "stuck", Host = "a", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 },
            new FtpSourceOptions { Name = "healthy", Host = "b", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var clientsCreated = 0;
        // Sources are polled in configuration order, so the first client belongs to "stuck".
        var poller = new TestRemotePoller(queue, opts, _ => clientsCreated++ == 0
            ? new FakeRemoteClient("a", 21, Array.Empty<FakeRemoteFile>(), listFailure: new TimeoutException("operation timed out"))
            : new FakeRemoteClient("b", 21, new[] { new FakeRemoteFile("/ok.txt", 1, mtime) }));

        var result = await poller.PollAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        var ev = Assert.Single(queue.TryDrain(10));
        Assert.EndsWith("/ok.txt", ev.Metadata.SourcePath);
    }

    [Fact]
    public async Task PollAsync_ListingFailure_KeepsFilesSeenBeforeFailureAndTriggersBackoff()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var clientsCreated = 0;
        var poller = new TestRemotePoller(queue, opts, _ =>
        {
            clientsCreated++;
            return new FakeRemoteClient("h", 21, new[] { new FakeRemoteFile("/first.txt", 1, mtime) }, listFailure: new TimeoutException("operation timed out"));
        });

        await poller.PollAsync(CancellationToken.None); // lists one file, then fails -> backoff
        await poller.PollAsync(CancellationToken.None); // inside backoff window -> source skipped

        Assert.Equal(1, clientsCreated);
        var ev = Assert.Single(queue.TryDrain(10));
        Assert.EndsWith("/first.txt", ev.Metadata.SourcePath);
    }

    [Fact]
    public async Task PollAsync_CancellationDuringListing_Propagates()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        // Shutdown is requested while the source is being polled; the listing then observes it and throws.
        using var cts = new CancellationTokenSource();
        var poller = new TestRemotePoller(new TestQueue(), opts, _ =>
        {
            cts.Cancel();
            return new FakeRemoteClient("h", 21, Array.Empty<FakeRemoteFile>(), listFailure: new OperationCanceledException(cts.Token));
        });

        // Shutdown cancellation is not a source failure: it must reach the host loop rather than be logged and swallowed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.PollAsync(cts.Token));
    }

    [Fact]
    public async Task PollAsync_NonCancellationErrorDuringShutdown_IsReportedAsCancellation()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        using var cts = new CancellationTokenSource();
        // SSH.NET teardown during shutdown surfaces as a connection/disposal error rather than a cancellation.
        var poller = new TestRemotePoller(new TestQueue(), opts, _ =>
        {
            cts.Cancel();
            return new FakeRemoteClient("h", 21, Array.Empty<FakeRemoteFile>(), listFailure: new ObjectDisposedException("session"));
        });

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.PollAsync(cts.Token));
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
    }

    [Fact]
    public async Task PollAsync_SourceFailures_IncrementPollSourceErrors()
    {
        var opts = CreateOptions(
            new FtpSourceOptions { Name = "list-fails", Host = "a", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 },
            new FtpSourceOptions { Name = "connect-fails", Host = "b", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var clientsCreated = 0;
        var poller = new TestRemotePoller(new TestQueue(), opts, _ => clientsCreated++ == 0
            ? new FakeRemoteClient("a", 21, Array.Empty<FakeRemoteFile>(), listFailure: new TimeoutException("operation timed out"))
            : new FakeRemoteClient("b", 21, Array.Empty<FakeRemoteFile>(), failConnect: true));

        var sources = await CaptureSourceErrorsAsync(() => poller.PollAsync(CancellationToken.None));

        Assert.Equal(new object?[] { "list-fails", "connect-fails" }, sources);
    }

    /// <summary>Runs <paramref name="act"/> and returns the <c>poll.source</c> tag of each <c>poll.source.errors</c> increment it caused.</summary>
    private static async Task<List<object?>> CaptureSourceErrorsAsync(Func<Task> act)
    {
        // The counter is process-wide; the AsyncLocal flag isolates this test's measurements from parallel tests.
        var inThisTest = new AsyncLocal<bool>();
        var sources = new List<object?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == TelemetryInstrumentation.MeterName && instrument.Name == "poll.source.errors")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (!inThisTest.Value) return;
            foreach (var tag in tags)
            {
                if (tag.Key == "poll.source") lock (sources) sources.Add(tag.Value);
            }
        });
        listener.Start();
        inThisTest.Value = true;

        await act();

        return sources;
    }

    [Fact]
    public async Task PollAsync_RejectedEnqueue_IsRetriedAfterQueuePause()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue { RejectNext = 1 };
        var time = new ManualTimeProvider();
        var file = new FakeRemoteFile("/a.txt", 5, DateTimeOffset.UtcNow.AddMinutes(-10));
        var clientsCreated = 0;
        var poller = new TestRemotePoller(queue, opts, _ =>
        {
            clientsCreated++;
            return new FakeRemoteClient("h", 21, new[] { file });
        }, time);

        await poller.PollAsync(CancellationToken.None); // queue rejects: nothing delivered, poller pauses
        await poller.PollAsync(CancellationToken.None); // inside the pause: the source is not contacted
        Assert.Equal(1, clientsCreated);
        Assert.Empty(queue.Events);

        time.Advance(TimeSpan.FromSeconds(6)); // first pause is 5s
        await poller.PollAsync(CancellationToken.None); // queue accepts: the unchanged file must be offered again
        await poller.PollAsync(CancellationToken.None); // and only once

        var ev = Assert.Single(queue.TryDrain(10));
        Assert.EndsWith("/a.txt", ev.Metadata.SourcePath);
    }

    [Fact]
    public async Task PollAsync_RejectedEnqueue_PausesAllSourcesWithoutBlamingAny()
    {
        var opts = CreateOptions(
            new FtpSourceOptions { Name = "first", Host = "a", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 },
            new FtpSourceOptions { Name = "second", Host = "b", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue { RejectNext = 1 };
        var time = new ManualTimeProvider();
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var clientsCreated = 0;
        var poller = new TestRemotePoller(queue, opts, _ =>
        {
            // Polled in configuration order: "first" (rejected), then after the pause "first" and "second".
            var host = ++clientsCreated == 3 ? "b" : "a";
            return new FakeRemoteClient(host, 21, new[] { new FakeRemoteFile("/a.txt", 1, mtime), new FakeRemoteFile("/b.txt", 1, mtime) });
        }, time);

        var errors = await CaptureSourceErrorsAsync(() => poller.PollAsync(CancellationToken.None));

        Assert.Empty(errors);           // the fault is the queue's: no source is counted as failing
        Assert.Equal(1, clientsCreated); // "second" is left until the pause is over
        Assert.Empty(queue.Events);      // /b.txt was not attempted after the rejection

        time.Advance(TimeSpan.FromSeconds(6));
        await poller.PollAsync(CancellationToken.None); // no source is in backoff, so both are polled straight away

        Assert.Equal(3, clientsCreated);
        Assert.Equal(4, queue.Events.Count);
    }

    [Fact]
    public async Task PollAsync_InvalidFile_IsSkippedWithoutBlockingTheRest()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var clientsCreated = 0;
        // FluentFTP reports size -1 when the server's listing has no parsable size; the queue's validator rejects that.
        var poller = new TestRemotePoller(queue, opts, _ =>
        {
            clientsCreated++;
            return new FakeRemoteClient("h", 21, new[] { new FakeRemoteFile("/bad.txt", -1, mtime), new FakeRemoteFile("/ok.txt", 1, mtime) });
        }, new ManualTimeProvider());

        var errors = await CaptureSourceErrorsAsync(async () =>
        {
            await poller.PollAsync(CancellationToken.None);
            await poller.PollAsync(CancellationToken.None); // neither paused nor in backoff: the source is polled again
        });

        Assert.Empty(errors);
        Assert.Equal(2, clientsCreated);
        var ev = Assert.Single(queue.TryDrain(10)); // the file listed after the invalid one still flows
        Assert.EndsWith("/ok.txt", ev.Metadata.SourcePath);
    }

    [Fact]
    public async Task PollAsync_UnclassifiedRejection_IsRetriedWithoutBlockingOtherFiles()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var refuseOdd = true;
        // Neither a Validation.* code nor marked transient: e.g. a custom validator or a default(Result).
        var queue = new TestQueue { RejectWith = ev => refuseOdd && ev.Metadata.SourcePath.EndsWith("/odd.txt") ? Error.Unspecified("Custom.Refused", "no") : null };
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, new[] { new FakeRemoteFile("/odd.txt", 1, mtime), new FakeRemoteFile("/ok.txt", 1, mtime) }), new ManualTimeProvider());

        var errors = await CaptureSourceErrorsAsync(() => poller.PollAsync(CancellationToken.None));

        Assert.Empty(errors);
        var ev = Assert.Single(queue.TryDrain(10)); // the file after the refused one flows, and the poller is not paused
        Assert.EndsWith("/ok.txt", ev.Metadata.SourcePath);

        refuseOdd = false;
        await poller.PollAsync(CancellationToken.None); // the refused file was not recorded, so it is offered again

        ev = Assert.Single(queue.TryDrain(10));
        Assert.EndsWith("/odd.txt", ev.Metadata.SourcePath);
    }

    [Fact]
    public async Task PollAsync_QueueThrows_IsNotASourceFailureAndTheFileIsRetried()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var throwForOdd = true;
        var queue = new TestQueue { ThrowFor = ev => throwForOdd && ev.Metadata.SourcePath.EndsWith("/odd.txt") ? new InvalidOperationException("validator blew up") : null };
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var clientsCreated = 0;
        var poller = new TestRemotePoller(queue, opts, _ =>
        {
            clientsCreated++;
            return new FakeRemoteClient("h", 21, new[] { new FakeRemoteFile("/odd.txt", 1, mtime), new FakeRemoteFile("/ok.txt", 1, mtime) });
        }, new ManualTimeProvider());

        var errors = await CaptureSourceErrorsAsync(() => poller.PollAsync(CancellationToken.None));

        Assert.Empty(errors);
        var ev = Assert.Single(queue.TryDrain(10));
        Assert.EndsWith("/ok.txt", ev.Metadata.SourcePath);

        throwForOdd = false;
        await poller.PollAsync(CancellationToken.None); // no backoff on the source, and the file is offered again

        Assert.Equal(2, clientsCreated);
        ev = Assert.Single(queue.TryDrain(10));
        Assert.EndsWith("/odd.txt", ev.Metadata.SourcePath);
    }

    [Fact]
    public async Task PollAsync_DefaultResultFromQueue_IsNotASourceFailure()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue { RejectWith = _ => default(Error) }; // what default(Result) carries: Code is null
        var file = new FakeRemoteFile("/a.txt", 1, DateTimeOffset.UtcNow.AddMinutes(-10));
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, new[] { file }), new ManualTimeProvider());

        var errors = await CaptureSourceErrorsAsync(() => poller.PollAsync(CancellationToken.None));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task PollAsync_RejectedEnqueue_StillResetsTheSourcesOwnBackoff()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var time = new ManualTimeProvider();
        var file = new FakeRemoteFile("/a.txt", 1, DateTimeOffset.UtcNow.AddMinutes(-10));
        var clientsCreated = 0;
        // Connect fails on polls 1 and 3; poll 2 connects and lists, but the queue rejects the file.
        var poller = new TestRemotePoller(queue, opts, _ =>
        {
            var call = ++clientsCreated;
            if (call == 2) queue.RejectNext = 1;
            return new FakeRemoteClient("h", 21, new[] { file }, failConnect: call is 1 or 3);
        }, time);

        await poller.PollAsync(CancellationToken.None); // connect failure 1 -> 5s backoff
        time.Advance(TimeSpan.FromSeconds(6));
        await poller.PollAsync(CancellationToken.None); // source is fine, queue rejects -> 5s queue pause
        time.Advance(TimeSpan.FromSeconds(6));
        await poller.PollAsync(CancellationToken.None); // connect failure: the first of a new streak -> 5s, not 10s
        time.Advance(TimeSpan.FromSeconds(6));
        await poller.PollAsync(CancellationToken.None);

        Assert.Equal(4, clientsCreated);
        Assert.Single(queue.Events);
    }

    [Fact]
    public async Task PollAsync_CycleWithoutRejection_EndsTheQueueRejectionStreak()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue { RejectNext = 2 };
        var time = new ManualTimeProvider();
        var file = new FakeRemoteFile("/a.txt", 1, DateTimeOffset.UtcNow.AddMinutes(-10));
        var clientsCreated = 0;
        // The rejected file is gone on poll 3, so that cycle has nothing to enqueue; it is back on poll 4.
        var poller = new TestRemotePoller(queue, opts, _ =>
        {
            var call = ++clientsCreated;
            if (call == 4) queue.RejectNext = 1;
            return new FakeRemoteClient("h", 21, call == 3 ? Array.Empty<FakeRemoteFile>() : new[] { file });
        }, time);

        await poller.PollAsync(CancellationToken.None); // rejection 1 -> 5s pause
        time.Advance(TimeSpan.FromSeconds(6));
        await poller.PollAsync(CancellationToken.None); // rejection 2 -> 10s pause
        time.Advance(TimeSpan.FromSeconds(11));
        await poller.PollAsync(CancellationToken.None); // nothing to enqueue: streak over
        await poller.PollAsync(CancellationToken.None); // rejected again: 5s, not 20s
        time.Advance(TimeSpan.FromSeconds(6));
        await poller.PollAsync(CancellationToken.None);

        Assert.Equal(5, clientsCreated);
        Assert.Single(queue.Events);
    }

    [Fact]
    public async Task PollAsync_RejectedEnqueue_StillObservesTheRestOfTheListing()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 1 });
        var queue = new TestQueue();
        var time = new ManualTimeProvider();
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var a = new FakeRemoteFile("/a.txt", 1, mtime);
        var b = new FakeRemoteFile("/b.txt", 1, mtime);
        var clientsCreated = 0;
        var poller = new TestRemotePoller(queue, opts, _ =>
        {
            var call = ++clientsCreated;
            if (call == 2) queue.RejectNext = 1;
            return new FakeRemoteClient("h", 21, call == 1 ? new[] { a } : new[] { a, b }); // b arrives on poll 2
        }, time);

        // The stability window runs on the wall clock, hence the real delays.
        await poller.PollAsync(CancellationToken.None); // a: first observation
        await Task.Delay(1100);
        await poller.PollAsync(CancellationToken.None); // a: stable but rejected; b: listed after it, first observation
        await Task.Delay(1100);
        time.Advance(TimeSpan.FromSeconds(6));
        await poller.PollAsync(CancellationToken.None); // b has been stable since poll 2, so it goes out with a

        Assert.Equal(new[] { "/a.txt", "/b.txt" }, queue.TryDrain(10).Select(e => e.Metadata.SourcePath[^6..]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // a secret-store timeout surfaces as TaskCanceledException although polling is not cancelled
    public async Task PollAsync_ClientCreationFailure_StaysWithItsSource(bool timeout)
    {
        var opts = CreateOptions(
            new FtpSourceOptions { Name = "no-secret", Host = "a", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 },
            new FtpSourceOptions { Name = "healthy", Host = "b", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var mtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var clientsRequested = 0;
        Exception failure = timeout ? new TaskCanceledException("secret lookup timed out") : new InvalidOperationException("secret store unreachable");
        // Sources are polled in configuration order, so the first request belongs to "no-secret".
        var poller = new TestRemotePoller(queue, opts, _ => clientsRequested++ == 0
            ? throw failure
            : new FakeRemoteClient("b", 21, new[] { new FakeRemoteFile("/ok.txt", 1, mtime) }), new ManualTimeProvider());

        var errors = await CaptureSourceErrorsAsync(async () =>
        {
            Assert.True((await poller.PollAsync(CancellationToken.None)).IsSuccess);
            await poller.PollAsync(CancellationToken.None); // "no-secret" is in backoff: no second creation attempt
        });

        Assert.Equal(new object?[] { "no-secret" }, errors);
        Assert.Equal(3, clientsRequested); // no-secret once, healthy twice
        var ev = Assert.Single(queue.TryDrain(10));
        Assert.EndsWith("/ok.txt", ev.Metadata.SourcePath);
    }

    [Theory]
    [InlineData(false)] // cancellation surfaces as OperationCanceledException
    [InlineData(true)]  // SSH.NET teardown surfaces as a non-cancellation error
    public async Task PollAsync_ShutdownDuringConnect_IsReportedAsCancellation(bool teardownError)
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        using var cts = new CancellationTokenSource();
        var poller = new TestRemotePoller(new TestQueue(), opts, _ =>
        {
            cts.Cancel();
            Exception failure = teardownError ? new ObjectDisposedException("session") : new OperationCanceledException(cts.Token);
            return new FakeRemoteClient("h", 21, Array.Empty<FakeRemoteFile>(), connectFailure: failure);
        });

        // Swallowing it would mean a "Failed to connect" warning, a poll.source.errors increment and backoff for a shutdown.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.PollAsync(cts.Token));
    }

    [Fact]
    public async Task ListSourceFoldersAsync_ReportsFolderKeysAndConnectFailures()
    {
        var opts = CreateOptions(
            new FtpSourceOptions { Name = "partners", Host = "h", Port = 21, RemotePath = "/partners", Pattern = "*.xml" },
            new FtpSourceOptions { Name = "down", Host = "x", Port = 21, RemotePath = "/" });
        var now = DateTimeOffset.UtcNow;
        var call = 0; // sources are listed in configuration order: "partners", then "down"
        var poller = new TestRemotePoller(new TestQueue(), opts, _ => call++ == 1
            ? new FakeRemoteClient("x", 21, [], failConnect: true)
            : new FakeRemoteClient("h", 21,
            [
                new FakeRemoteFile("/partners/acme", 0, now, IsDir: true),
                new FakeRemoteFile("/partners/acme/ORDRSP", 0, now, IsDir: true),
                new FakeRemoteFile("/partners/acme/INVOIC", 0, now, IsDir: true),
                new FakeRemoteFile("/partners/acme/INVOIC/1.xml", 5, now),
            ]));

        var listings = await poller.ListSourceFoldersAsync(CancellationToken.None);

        Assert.Equal(2, listings.Count);
        var ok = listings[0];
        Assert.Equal("partners", ok.Source);
        Assert.Equal("ftp", ok.Protocol);
        Assert.Equal("*.xml", ok.Pattern);
        Assert.Null(ok.Error);
        Assert.Equal(["ftp://h:21/partners/acme/INVOIC", "ftp://h:21/partners/acme/ORDRSP"], ok.Folders);

        var failed = listings[1];
        Assert.Equal("down", failed.Source);
        Assert.Equal("connect fail", failed.Error);
        Assert.Empty(failed.Folders);
    }

    [Fact]
    public async Task ListSourceFoldersAsync_CancelledMidWalk_Throws()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "partners", Host = "h", Port = 21, RemotePath = "/partners" });
        using var cts = new CancellationTokenSource();
        var now = DateTimeOffset.UtcNow;
        // The fake ignores the token, like a real client that ends the listing quietly on cancellation.
        var poller = new TestRemotePoller(new TestQueue(), opts, _ =>
        {
            cts.Cancel();
            return new FakeRemoteClient("h", 21, [new FakeRemoteFile("/partners/acme", 0, now, IsDir: true)]);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.ListSourceFoldersAsync(cts.Token));
    }
}
