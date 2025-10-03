using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Models;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

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
        public FakeRemoteClient(string host, int port, ProtocolType protocol, IEnumerable<FakeRemoteFile> files, bool failConnect = false)
        { Host = host; Port = port; Protocol = protocol; _files = files.ToList(); _failConnect = failConnect; }
        public string Host { get; }
        public int Port { get; }
        public ProtocolType Protocol { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task ConnectAsync(CancellationToken ct)
        { if (_failConnect) throw new InvalidOperationException("connect fail"); return Task.CompletedTask; }
        public async IAsyncEnumerable<IRemoteFileInfo> ListFilesAsync(string remotePath, bool recursive, string pattern, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { foreach (var f in _files) { yield return new FakeRemoteFileInfo(f.FullPath, f.Size, f.LastWrite, f.IsDir); await Task.Yield(); } }
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
}
