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
        public FakeRemoteClient(string host, int port, ProtocolType protocol, IEnumerable<FakeRemoteFile> files, bool failConnect = false, Exception? listFailure = null)
        { Host = host; Port = port; Protocol = protocol; _files = files.ToList(); _failConnect = failConnect; _listFailure = listFailure; }
        public string Host { get; }
        public int Port { get; }
        public ProtocolType Protocol { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task ConnectAsync(CancellationToken ct)
        { if (_failConnect) throw new InvalidOperationException("connect fail"); return Task.CompletedTask; }
        public async IAsyncEnumerable<IRemoteFileInfo> ListFilesAsync(string remotePath, bool recursive, string pattern, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var f in _files) { yield return new FakeRemoteFileInfo(f.FullPath, f.Size, f.LastWrite, f.IsDir); await Task.Yield(); }
            if (_listFailure is not null) throw _listFailure; // fails after the listed files, like a stalled READDIR batch
        }
        public Task<IRemoteFileInfo?> GetFileInfoAsync(string path, CancellationToken ct) => Task.FromResult<IRemoteFileInfo?>(null); // not used
        public Task DeleteAsync(string fullPath, CancellationToken ct) => Task.CompletedTask; // deletion not exercised in these tests
    }

    private sealed class TestQueue : IFileEventQueue
    {
        public readonly ConcurrentQueue<FileEvent> Events = new();
        public Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct)
        { Events.Enqueue(fileEvent); return Task.FromResult(Result.Success()); }
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
        internal TestRemotePoller(IFileEventQueue queue, IOptionsMonitor<RemoteFileSourcesOptions> opts, Func<object, IRemoteFileClient> clientFactory)
            : base(queue, opts, NullLogger<TestRemotePoller>.Instance)
        { _clientFactory = clientFactory; _sources = new(); foreach (var f in opts.CurrentValue.Ftp) if (f.Enabled) _sources.Add(new Src(f.Name, f.RemotePath, f.Pattern, f.Recursive, f.MinStableSeconds, f.DestinationPath, f.Host, f.Port)); }
        protected override List<IRemoteFileSourceDescriptor> GetEnabledSources() => _sources;
        protected override IRemoteFileClient CreateClient(IRemoteFileSourceDescriptor source) => _clientFactory(source);
        protected override ProtocolType MapProtocolType(ProtocolType protocol) => protocol; // identity mapping
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
        }
    }

    private static OptionsMonitorStub<RemoteFileSourcesOptions> CreateOptions(params FtpSourceOptions[] ftp)
        => new(new RemoteFileSourcesOptions { Ftp = ftp.ToList(), Sftp = new() });

    [Fact]
    public async Task PollAsync_StableFile_EnqueuesOnce()
    {
        var opts = CreateOptions(new FtpSourceOptions { Name = "f1", Host = "h", Port = 21, RemotePath = "/", Pattern = "*", MinStableSeconds = 0 });
        var queue = new TestQueue();
        var file = new FakeRemoteFile("/a.txt", 5, DateTimeOffset.UtcNow.AddSeconds(-10));
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, ProtocolType.Ftp, new[] { file }));

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
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, ProtocolType.Ftp, filesSequence[Math.Min(call++, filesSequence.Count - 1)]));

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
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, ProtocolType.Ftp, versions[Math.Min(call++, versions.Count - 1)]));

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
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, ProtocolType.Ftp, new[] { file }));

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
        var poller = new TestRemotePoller(queue, opts, _ => new FakeRemoteClient("h", 21, ProtocolType.Ftp, Array.Empty<FakeRemoteFile>(), failConnect: true));

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
            ? new FakeRemoteClient("a", 21, ProtocolType.Ftp, Array.Empty<FakeRemoteFile>(), listFailure: new TimeoutException("operation timed out"))
            : new FakeRemoteClient("b", 21, ProtocolType.Ftp, new[] { new FakeRemoteFile("/ok.txt", 1, mtime) }));

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
            return new FakeRemoteClient("h", 21, ProtocolType.Ftp, new[] { new FakeRemoteFile("/first.txt", 1, mtime) }, listFailure: new TimeoutException("operation timed out"));
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
            return new FakeRemoteClient("h", 21, ProtocolType.Ftp, Array.Empty<FakeRemoteFile>(), listFailure: new OperationCanceledException(cts.Token));
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
            return new FakeRemoteClient("h", 21, ProtocolType.Ftp, Array.Empty<FakeRemoteFile>(), listFailure: new ObjectDisposedException("session"));
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
            ? new FakeRemoteClient("a", 21, ProtocolType.Ftp, Array.Empty<FakeRemoteFile>(), listFailure: new TimeoutException("operation timed out"))
            : new FakeRemoteClient("b", 21, ProtocolType.Ftp, Array.Empty<FakeRemoteFile>(), failConnect: true));

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

        await poller.PollAsync(CancellationToken.None);

        Assert.Equal(new object?[] { "list-fails", "connect-fails" }, sources);
    }
}
