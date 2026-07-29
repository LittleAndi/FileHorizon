using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Idempotency;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Models;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace FileHorizon.Application.Tests;

public class IdempotencySeedTests
{
    private const string SourceName = "s1";
    private const string Host = "sftp.example.com";
    private const int Port = 22;

    [Fact]
    public async Task SeedIdempotencyAsync_ProducesTheSameKeyAsTheTransferPath()
    {
        // The whole point of seeding is that a seeded marker suppresses the file the poller would
        // otherwise dispatch, so the two code paths must derive byte-identical keys.
        var file = new FakeFile("/upload/a.zip", 1234, new DateTimeOffset(2026, 7, 14, 9, 13, 22, TimeSpan.Zero));
        var queue = new TestQueue();
        var poller = CreatePoller(queue, file, minStableSeconds: 0);

        var seed = await poller.SeedIdempotencyAsync(
            new IdempotencySeedRequest(SourceName, DryRun: true), new InMemoryIdempotencyStore(), CancellationToken.None);
        Assert.True(seed.IsSuccess);
        var seededKey = Assert.Single(seed.Value!.SampleKeys);

        await poller.PollAsync(CancellationToken.None);
        var dispatched = Assert.Single(queue.TryDrain(10));
        var transferKey = FileIdentity.BuildIdempotencyKey(dispatched.Metadata);

        Assert.Equal(transferKey, seededKey);
    }

    [Fact]
    public async Task SeedIdempotencyAsync_MarksScannedFilesAsProcessed()
    {
        var file = new FakeFile("/upload/a.zip", 10, DateTimeOffset.UtcNow.AddHours(-1));
        var queue = new TestQueue();
        var poller = CreatePoller(queue, file, minStableSeconds: 0);
        var store = new InMemoryIdempotencyStore();

        var seed = await poller.SeedIdempotencyAsync(
            new IdempotencySeedRequest(SourceName), store, CancellationToken.None);

        Assert.True(seed.IsSuccess);
        Assert.Equal(1, seed.Value!.Scanned);
        Assert.Equal(1, seed.Value.Marked);
        Assert.Equal(0, seed.Value.AlreadyMarked);

        // A subsequent poll produces the key the orchestrator would look up; it must already be present.
        await poller.PollAsync(CancellationToken.None);
        var dispatched = Assert.Single(queue.TryDrain(10));
        Assert.True(await store.IsProcessedAsync(FileIdentity.BuildIdempotencyKey(dispatched.Metadata), CancellationToken.None));
    }

    [Fact]
    public async Task SeedIdempotencyAsync_RunTwice_ReportsSecondPassAsAlreadyMarked()
    {
        var file = new FakeFile("/upload/a.zip", 10, DateTimeOffset.UtcNow.AddHours(-1));
        var poller = CreatePoller(new TestQueue(), file, minStableSeconds: 0);
        var store = new InMemoryIdempotencyStore();

        await poller.SeedIdempotencyAsync(new IdempotencySeedRequest(SourceName), store, CancellationToken.None);
        var second = await poller.SeedIdempotencyAsync(new IdempotencySeedRequest(SourceName), store, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(0, second.Value!.Marked);
        Assert.Equal(1, second.Value.AlreadyMarked);
    }

    [Fact]
    public async Task SeedIdempotencyAsync_DryRun_WritesNothing()
    {
        var file = new FakeFile("/upload/a.zip", 10, DateTimeOffset.UtcNow.AddHours(-1));
        var poller = CreatePoller(new TestQueue(), file, minStableSeconds: 0);
        var store = new InMemoryIdempotencyStore();

        var seed = await poller.SeedIdempotencyAsync(
            new IdempotencySeedRequest(SourceName, DryRun: true), store, CancellationToken.None);

        Assert.True(seed.IsSuccess);
        Assert.Equal(1, seed.Value!.Scanned);
        Assert.Equal(0, seed.Value.Marked);
        Assert.False(await store.IsProcessedAsync(seed.Value.SampleKeys[0], CancellationToken.None));
    }

    [Fact]
    public async Task SeedIdempotencyAsync_PatternOverride_LimitsScope()
    {
        var files = new[]
        {
            new FakeFile("/upload/a.zip", 10, DateTimeOffset.UtcNow.AddHours(-1)),
            new FakeFile("/upload/b.txt", 10, DateTimeOffset.UtcNow.AddHours(-1))
        };
        var poller = CreatePoller(new TestQueue(), files, minStableSeconds: 0, pattern: "*");
        var store = new InMemoryIdempotencyStore();

        var seed = await poller.SeedIdempotencyAsync(
            new IdempotencySeedRequest(SourceName, PatternOverride: "*.zip"), store, CancellationToken.None);

        Assert.True(seed.IsSuccess);
        Assert.Equal(1, seed.Value!.Scanned);
        Assert.Equal("*.zip", seed.Value.Pattern);
        Assert.Contains("a.zip", seed.Value.SampleKeys[0]);
    }

    [Fact]
    public async Task SeedIdempotencyAsync_KeyHasTheDocumentedShape()
    {
        // Pins the on-disk marker format. Note the mtime renders with a "+00:00" offset rather than "Z",
        // because FileMetadata.LastModifiedUtc is a DateTimeOffset.
        var mtime = new DateTimeOffset(2026, 7, 14, 9, 13, 22, TimeSpan.Zero);
        var poller = CreatePoller(new TestQueue(), new FakeFile("/upload/a.zip", 1234, mtime), minStableSeconds: 0);

        var seed = await poller.SeedIdempotencyAsync(
            new IdempotencySeedRequest(SourceName, DryRun: true), new InMemoryIdempotencyStore(), CancellationToken.None);

        Assert.True(seed.IsSuccess);
        Assert.Equal(
            "fh:idemp:v2:sftp://sftp.example.com:22/upload/a.zip|1234|2026-07-14T09:13:22.0000000+00:00",
            seed.Value!.SampleKeys[0]);
    }

    [Fact]
    public async Task SeedIdempotencyAsync_SkipsDirectories()
    {
        var entries = new[]
        {
            new FakeFile("/upload/nested", 0, DateTimeOffset.UtcNow.AddHours(-1), IsDir: true),
            new FakeFile("/upload/a.zip", 10, DateTimeOffset.UtcNow.AddHours(-1))
        };
        var poller = CreatePoller(new TestQueue(), entries, minStableSeconds: 0);
        var store = new InMemoryIdempotencyStore();

        var seed = await poller.SeedIdempotencyAsync(
            new IdempotencySeedRequest(SourceName), store, CancellationToken.None);

        Assert.True(seed.IsSuccess);
        Assert.Equal(1, seed.Value!.Scanned);
    }

    [Fact]
    public async Task SeedIdempotencyAsync_UnknownSource_Fails()
    {
        var file = new FakeFile("/upload/a.zip", 10, DateTimeOffset.UtcNow.AddHours(-1));
        var poller = CreatePoller(new TestQueue(), file, minStableSeconds: 0);

        var seed = await poller.SeedIdempotencyAsync(
            new IdempotencySeedRequest("nope"), new InMemoryIdempotencyStore(), CancellationToken.None);

        Assert.True(seed.IsFailure);
        Assert.Contains("nope", seed.Error.Message);
    }

    [Fact]
    public async Task SeedIdempotencyAsync_ConnectFailure_ReturnsFailure()
    {
        var file = new FakeFile("/upload/a.zip", 10, DateTimeOffset.UtcNow.AddHours(-1));
        var poller = CreatePoller(new TestQueue(), [file], minStableSeconds: 0, failConnect: true);

        var seed = await poller.SeedIdempotencyAsync(
            new IdempotencySeedRequest(SourceName), new InMemoryIdempotencyStore(), CancellationToken.None);

        Assert.True(seed.IsFailure);
        Assert.Equal("Idempotency.SeedFailed", seed.Error.Code);
    }

    private static TestRemotePoller CreatePoller(
        TestQueue queue,
        FakeFile file,
        int minStableSeconds) => CreatePoller(queue, [file], minStableSeconds);

    private static TestRemotePoller CreatePoller(
        TestQueue queue,
        FakeFile[] files,
        int minStableSeconds,
        string pattern = "*.zip",
        bool failConnect = false)
    {
        var options = new OptionsMonitorStub<RemoteFileSourcesOptions>(new RemoteFileSourcesOptions
        {
            Sftp =
            [
                new SftpSourceOptions
                {
                    Name = SourceName,
                    Host = Host,
                    Port = Port,
                    RemotePath = "/upload",
                    Pattern = pattern,
                    Recursive = false,
                    MinStableSeconds = minStableSeconds,
                    Username = "u",
                    PasswordSecretRef = "p"
                }
            ]
        });
        return new TestRemotePoller(queue, options, _ => new FakeRemoteClient(Host, Port, files, failConnect));
    }

    private sealed record FakeFile(string FullPath, long Size, DateTimeOffset LastWrite, bool IsDir = false);

    private sealed class FakeRemoteFileInfo(FakeFile file) : IRemoteFileInfo
    {
        public string FullPath { get; } = file.FullPath;
        public string Name { get; } = Path.GetFileName(file.FullPath);
        public long Size { get; } = file.Size;
        public DateTimeOffset LastWriteTimeUtc { get; } = file.LastWrite;
        public bool IsDirectory { get; } = file.IsDir;
    }

    private sealed class FakeRemoteClient(string host, int port, FakeFile[] files, bool failConnect) : IRemoteFileClient
    {
        public ProtocolType Protocol => ProtocolType.Sftp;
        public string Host { get; } = host;
        public int Port { get; } = port;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task ConnectAsync(CancellationToken ct)
            => failConnect ? throw new InvalidOperationException("connect fail") : Task.CompletedTask;

        public async IAsyncEnumerable<IRemoteFileInfo> ListFilesAsync(
            string path, bool recursive, string pattern,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var f in files)
            {
                // Mirror the real client: the glob is applied to the bare file name, directories pass through.
                if (!f.IsDir && !Matches(Path.GetFileName(f.FullPath), pattern)) continue;
                yield return new FakeRemoteFileInfo(f);
                await Task.Yield();
            }
        }

        private static bool Matches(string name, string pattern)
        {
            if (pattern is "*" or "*.*") return true;
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        public Task<IRemoteFileInfo?> GetFileInfoAsync(string fullPath, CancellationToken ct)
            => Task.FromResult<IRemoteFileInfo?>(null);

        public Task DeleteAsync(string fullPath, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestQueue : IFileEventQueue
    {
        private readonly ConcurrentQueue<FileEvent> _events = new();

        public Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct)
        {
            _events.Enqueue(fileEvent);
            return Task.FromResult(Result.Success());
        }

        public IAsyncEnumerable<FileEvent> DequeueAsync(CancellationToken ct) => Empty();

        private static async IAsyncEnumerable<FileEvent> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        public IReadOnlyCollection<FileEvent> TryDrain(int maxCount)
        {
            var list = new List<FileEvent>();
            while (list.Count < maxCount && _events.TryDequeue(out var ev)) list.Add(ev);
            return list;
        }
    }

    private sealed class TestRemotePoller : RemotePollerBase
    {
        private readonly List<IRemoteFileSourceDescriptor> _sources;
        // object rather than IRemoteFileSourceDescriptor: the descriptor interface is protected on the
        // base, so the enclosing test class cannot name it.
        private readonly Func<object, IRemoteFileClient> _clientFactory;

        internal TestRemotePoller(
            IFileEventQueue queue,
            IOptionsMonitor<RemoteFileSourcesOptions> options,
            Func<object, IRemoteFileClient> clientFactory)
            : base(queue, options, NullLogger<TestRemotePoller>.Instance)
        {
            _clientFactory = clientFactory;
            _sources = [.. options.CurrentValue.Sftp.Where(s => s.Enabled).Select(s => (IRemoteFileSourceDescriptor)new Src(s))];
        }

        protected override List<IRemoteFileSourceDescriptor> GetEnabledSources() => _sources;
        protected override IRemoteFileClient CreateClient(IRemoteFileSourceDescriptor source) => _clientFactory(source);
        protected override ProtocolType MapProtocolType(ProtocolType protocol) => protocol;

        private sealed class Src(SftpSourceOptions o) : IRemoteFileSourceDescriptor
        {
            public string Name => o.Name;
            public string RemotePath => o.RemotePath;
            public string Pattern => o.Pattern;
            public bool Recursive => o.Recursive;
            public int MinStableSeconds => o.MinStableSeconds;
            public string? DestinationPath => o.DestinationPath;
            public string Host => o.Host;
            public int Port => o.Port;
            public bool DeleteAfterTransfer => o.DeleteAfterTransfer;
        }
    }
}
