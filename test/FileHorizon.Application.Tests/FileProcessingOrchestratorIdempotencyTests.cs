using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FileHorizon.Application.Tests;

public class FileProcessingOrchestratorIdempotencyTests : IDisposable
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
        where T : class, new()
    {
        private readonly T _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class FakePublisher : IFileContentPublisher
    {
        public Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct)
            => Task.FromResult(Result.Success());
    }

    private readonly string _srcFile;
    private readonly string _destRoot;

    public FileProcessingOrchestratorIdempotencyTests()
    {
        _srcFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        _destRoot = Path.Combine(Path.GetTempPath(), "orchestrator-idemp-tests", Guid.NewGuid().ToString("N"));
        File.WriteAllText(_srcFile, "hello idempotency");
    }

    public void Dispose()
    {
        try { File.Delete(_srcFile); } catch { }
        try { Directory.Delete(_destRoot, true); } catch { }
    }

    private FileEvent NewEvent(DateTimeOffset? mtime = null) => new(
        Id: Guid.NewGuid().ToString("N"),
        Metadata: new FileMetadata(_srcFile, new FileInfo(_srcFile).Length, mtime ?? new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero), "none", null),
        DiscoveredAtUtc: DateTimeOffset.UtcNow,
        Protocol: "local",
        DestinationPath: string.Empty,
        DeleteAfterTransfer: false);

    private FileProcessingOrchestrator CreateOrchestrator(IIdempotencyStore store, bool enabled = true, bool overwrite = true)
    {
        var routing = new RoutingOptions
        {
            Rules =
            [
                new RoutingRuleOptions
                {
                    Name = "local",
                    Protocol = "local",
                    PathGlob = "**/*.txt",
                    Destinations = ["OutboxA"],
                    Overwrite = overwrite,
                    RenamePattern = "{fileName}"
                }
            ]
        };
        var router = new Infrastructure.Processing.SimpleFileRouter(
            routingOptions: new StaticOptionsMonitor<RoutingOptions>(routing),
            destinationsOptions: new StaticOptionsMonitor<DestinationsOptions>(new DestinationsOptions()),
            logger: NullLogger<Infrastructure.Processing.SimpleFileRouter>.Instance);

        var destinations = new DestinationsOptions
        {
            Local = [new LocalDestinationOptions { Name = "OutboxA", RootPath = _destRoot }]
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        return new FileProcessingOrchestrator(
            router: router,
            readers: [new Infrastructure.Processing.LocalFileContentReader(NullLogger<Infrastructure.Processing.LocalFileContentReader>.Instance)],
            sinks: [new Infrastructure.Processing.LocalFileSink(NullLogger<Infrastructure.Processing.LocalFileSink>.Instance)],
            destinations: new StaticOptionsMonitor<DestinationsOptions>(destinations),
            idempotencyOptions: new StaticOptionsMonitor<IdempotencyOptions>(new IdempotencyOptions { Enabled = enabled, TtlSeconds = 0 }),
            remoteSources: new StaticOptionsMonitor<RemoteFileSourcesOptions>(new RemoteFileSourcesOptions()),
            idempotencyStore: store,
            sftpFactory: new Infrastructure.Remote.SshNetSftpClientFactory(NullLogger<Infrastructure.Remote.SshNetSftpClientFactory>.Instance),
            secretResolver: new Infrastructure.Secrets.InMemorySecretResolver(configuration, NullLogger<Infrastructure.Secrets.InMemorySecretResolver>.Instance),
            sftpClientLogger: NullLogger<Infrastructure.Remote.SftpRemoteFileClient>.Instance,
            ftpClientLogger: NullLogger<Infrastructure.Remote.FtpRemoteFileClient>.Instance,
            publisher: new FakePublisher(),
            fileTypeDetector: new Infrastructure.Processing.ExtensionFileTypeDetector(),
            logger: NullLogger<FileProcessingOrchestrator>.Instance);
    }

    private string DestFile => Path.Combine(_destRoot, Path.GetFileName(_srcFile));

    [Fact]
    public async Task SecondEvent_SameIdentityDifferentGuid_IsSkipped()
    {
        var store = new Infrastructure.Idempotency.InMemoryIdempotencyStore();
        var orchestrator = CreateOrchestrator(store);

        var first = await orchestrator.ProcessAsync(NewEvent(), CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.True(File.Exists(DestFile));

        // Simulate re-discovery after a restart: new GUID, same file identity.
        File.Delete(DestFile);
        var second = await orchestrator.ProcessAsync(NewEvent(), CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.False(File.Exists(DestFile)); // skipped, nothing written
    }

    [Fact]
    public async Task ChangedMtime_IsProcessedAgain()
    {
        var store = new Infrastructure.Idempotency.InMemoryIdempotencyStore();
        var orchestrator = CreateOrchestrator(store);

        var mtime = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.True((await orchestrator.ProcessAsync(NewEvent(mtime), CancellationToken.None)).IsSuccess);

        File.Delete(DestFile);
        Assert.True((await orchestrator.ProcessAsync(NewEvent(mtime.AddMinutes(5)), CancellationToken.None)).IsSuccess);

        Assert.True(File.Exists(DestFile)); // new version transferred
    }

    [Fact]
    public async Task FailedTransfer_DoesNotMark_RetrySucceeds()
    {
        var store = new Infrastructure.Idempotency.InMemoryIdempotencyStore();
        // Overwrite=false + pre-existing destination file => LocalFileSink fails with CreateNew collision.
        var failing = CreateOrchestrator(store, overwrite: false);
        Directory.CreateDirectory(_destRoot);
        File.WriteAllText(DestFile, "occupied");

        var ev = NewEvent();
        var failed = await failing.ProcessAsync(ev, CancellationToken.None);
        Assert.True(failed.IsFailure);
        Assert.False(await store.IsProcessedAsync(FileIdentity.BuildIdempotencyKey(ev.Metadata), CancellationToken.None));

        // Clear the collision; the same identity must be retryable.
        File.Delete(DestFile);
        var retried = await failing.ProcessAsync(NewEvent(), CancellationToken.None);
        Assert.True(retried.IsSuccess);
        Assert.True(await store.IsProcessedAsync(FileIdentity.BuildIdempotencyKey(ev.Metadata), CancellationToken.None));
    }

    [Fact]
    public async Task StoreCheckThrows_ProcessingProceeds()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.IsProcessedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store down"));
        store.TryMarkProcessedAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var orchestrator = CreateOrchestrator(store);

        var result = await orchestrator.ProcessAsync(NewEvent(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(DestFile)); // fail-open: transferred despite check failure
    }

    [Fact]
    public async Task MarkThrowsAfterSuccessfulTransfer_ResultIsStillSuccess()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.IsProcessedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        store.TryMarkProcessedAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store down"));
        var orchestrator = CreateOrchestrator(store);

        var result = await orchestrator.ProcessAsync(NewEvent(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(DestFile));
    }

    [Fact]
    public async Task Disabled_StoreIsNeverCalled()
    {
        var store = Substitute.For<IIdempotencyStore>();
        var orchestrator = CreateOrchestrator(store, enabled: false);

        var result = await orchestrator.ProcessAsync(NewEvent(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await store.DidNotReceive().IsProcessedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().TryMarkProcessedAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuccessfulTransfer_MarksWithNullTtl_WhenTtlSecondsIsZero()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.IsProcessedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        store.TryMarkProcessedAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(true);
        var orchestrator = CreateOrchestrator(store);

        var ev = NewEvent();
        var result = await orchestrator.ProcessAsync(ev, CancellationToken.None);

        Assert.True(result.IsSuccess);
        await store.Received(1).TryMarkProcessedAsync(
            FileIdentity.BuildIdempotencyKey(ev.Metadata),
            null,
            Arg.Any<CancellationToken>());
    }
}
