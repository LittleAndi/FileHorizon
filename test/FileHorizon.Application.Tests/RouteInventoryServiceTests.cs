using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Infrastructure.Telemetry;
using FileHorizon.Application.Models;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class RouteInventoryServiceTests
{
    private const string Root = "sftp://sftp.example.com:22/partners";

    private readonly CapturingLogger<RouteInventoryService> _logger = new();
    private readonly StubLister _lister = new();

    private RouteInventoryService Create(
        RoutingOptions? routing = null,
        TimeSpan? interval = null,
        PipelineRole role = PipelineRole.All)
        => new(
            () => _lister,
            Options.Create(new TelemetryOptions { RouteInventoryInterval = interval }),
            Options.Create(new PipelineOptions { Role = role }),
            new OptionsMonitorStub<RoutingOptions>(routing ?? Rules()),
            new OptionsMonitorStub<DestinationsOptions>(new DestinationsOptions
            {
                ServiceBus = [new ServiceBusDestinationOptions { Name = "OrdersBus", EntityName = "orders-in" }],
                Local = [new LocalDestinationOptions { Name = "Archive", RootPath = "/archive" }],
            }),
            _logger);

    private static RoutingOptions Rules() => new()
    {
        Rules =
        [
            new RoutingRuleOptions { Name = "ordrsp", Protocol = "sftp", PathGlob = "**/ORDRSP/*.xml", Destinations = ["OrdersBus"] },
            // A regex is always pattern-dependent; the glob is what confines the rule to its folder.
            new RoutingRuleOptions { Name = "invoic", Protocol = "sftp", PathGlob = "**/INVOIC/*.xml", PathRegex = @"/\d+\.xml$", Destinations = ["Archive"] },
        ]
    };

    private CapturingLogger<RouteInventoryService>.Entry Single(string prefix, string folder)
        => Assert.Single(_logger.Entries, e => e.Message.StartsWith(prefix, StringComparison.Ordinal) && Equals(e.State["Folder"], folder));

    [Fact]
    public async Task RunOnce_LogsResolvedAndUnresolvedFolders_WithSummary()
    {
        _lister.Listings.Add(new SourceFolderListing("partners", "sftp", "*.xml", '/',
            [$"{Root}/acme/ORDRSP", $"{Root}/acme/INVOIC", $"{Root}/acme/DESADV"]));

        await Create().RunOnceAsync(CancellationToken.None);

        var ordrsp = Single("Route resolved", $"{Root}/acme/ORDRSP");
        Assert.Equal(LogLevel.Information, ordrsp.Level);
        Assert.Equal("partners", ordrsp.State["Source"]);
        Assert.Equal("ordrsp", ordrsp.State["Rule"]);
        Assert.Equal("OrdersBus", ordrsp.State["Destination"]);
        Assert.Equal("ServiceBus", ordrsp.State["Kind"]);
        Assert.Equal("orders-in", ordrsp.State["Entity"]);
        Assert.Equal("exact", ordrsp.State["Match"]);

        var invoic = Single("Route resolved", $"{Root}/acme/INVOIC");
        Assert.Equal("invoic", invoic.State["Rule"]);
        Assert.Equal("/archive", invoic.State["Entity"]);
        Assert.Equal("pattern-dependent", invoic.State["Match"]);

        Assert.Equal(LogLevel.Warning, Single("Route unresolved", $"{Root}/acme/DESADV").Level);

        var summary = Assert.Single(_logger.Entries, e => e.Message.StartsWith("Route inventory partners", StringComparison.Ordinal));
        Assert.Equal(3, summary.State["Folders"]);
        Assert.Equal(2, summary.State["Resolved"]);
        Assert.Equal(1, summary.State["Unresolved"]);
    }

    [Fact]
    public async Task RunOnce_FirstMatchingRuleWins()
    {
        var routing = Rules();
        routing.Rules.Insert(0, new RoutingRuleOptions { Name = "catch-all", PathGlob = "**/*", Destinations = ["Archive"] });
        _lister.Listings.Add(new SourceFolderListing("partners", "sftp", "*.xml", '/', [$"{Root}/acme/ORDRSP"]));

        await Create(routing).RunOnceAsync(CancellationToken.None);

        Assert.Equal("catch-all", Single("Route resolved", $"{Root}/acme/ORDRSP").State["Rule"]);
    }

    [Fact]
    public async Task RunOnce_UnknownDestination_IsReportedAsUnknownKind()
    {
        var routing = new RoutingOptions { Rules = [new RoutingRuleOptions { Name = "typo", PathGlob = "**/*", Destinations = ["Nope"] }] };
        _lister.Listings.Add(new SourceFolderListing("partners", "sftp", "*.*", '/', [Root]));

        await Create(routing).RunOnceAsync(CancellationToken.None);

        var entry = Single("Route resolved", Root);
        Assert.Equal("Unknown", entry.State["Kind"]);
        Assert.Null(entry.State["Entity"]);
    }

    [Fact]
    public async Task RunOnce_ListingError_IsAWarningAndOtherSourcesStillRun()
    {
        _lister.Listings.Add(SourceFolderListing.Failed("down", "sftp", "*.*", '/', "connect fail"));
        _lister.Listings.Add(new SourceFolderListing("partners", "sftp", "*.xml", '/', [$"{Root}/acme/ORDRSP"]));

        await Create().RunOnceAsync(CancellationToken.None);

        var error = Assert.Single(_logger.Entries, e => e.Message.StartsWith("Route inventory could not list", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, error.Level);
        Assert.Equal("down", error.State["Source"]);
        Single("Route resolved", $"{Root}/acme/ORDRSP");
    }

    [Fact]
    public async Task RunOnce_NoRules_WarnsOnceAndSkipsTheWalk()
    {
        _lister.Listings.Add(new SourceFolderListing("partners", "sftp", "*.xml", '/', [$"{Root}/a", $"{Root}/b"]));

        await Create(new RoutingOptions()).RunOnceAsync(CancellationToken.None);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("No routing rules on this instance", entry.Message);
        Assert.Equal(0, _lister.Calls);
    }

    [Fact]
    public async Task Execute_FailureDuringShutdown_IsNotLoggedAsAnError()
    {
        var blocking = new BlockingLister();
        var service = new RouteInventoryService(
            () => blocking,
            Options.Create(new TelemetryOptions { RouteInventoryInterval = TimeSpan.FromHours(1) }),
            Options.Create(new PipelineOptions()),
            new OptionsMonitorStub<RoutingOptions>(Rules()),
            new OptionsMonitorStub<DestinationsOptions>(new DestinationsOptions()),
            _logger);

        await service.StartAsync(CancellationToken.None);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task MultiProtocolPoller_OneListerFailing_DoesNotHideTheOthers()
    {
        var logger = new CapturingLogger<MultiProtocolPoller>();
        var ok = new StubPoller([new SourceFolderListing("ok", "sftp", "*.*", '/', [Root])]);
        var poller = new MultiProtocolPoller([new StubPoller(null), ok], logger);

        var listings = await poller.ListSourceFoldersAsync(CancellationToken.None);

        Assert.Equal("ok", Assert.Single(listings).Source);
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
    }

    [Theory]
    [InlineData(null, PipelineRole.All)]
    [InlineData(60, PipelineRole.Worker)]
    public async Task Execute_DisabledOrWorker_NeverListsFolders(int? intervalMinutes, PipelineRole role)
    {
        var service = Create(interval: intervalMinutes is { } m ? TimeSpan.FromMinutes(m) : null, role: role);

        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, _lister.Calls);
    }

    [Fact]
    public async Task Execute_Enabled_RunsAtStartup()
    {
        _lister.Listings.Add(new SourceFolderListing("partners", "sftp", "*.xml", '/', [$"{Root}/acme/ORDRSP"]));
        var service = Create(interval: TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);
        await _lister.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, _lister.Calls);
    }

    [Fact]
    public async Task LocalDirectoryPoller_ListsLeavesAndFoldersWithFiles()
    {
        var root = Directory.CreateTempSubdirectory("fh-inventory-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "acme", "ORDRSP"));
            Directory.CreateDirectory(Path.Combine(root, "acme", "INVOIC", "old"));
            File.WriteAllText(Path.Combine(root, "acme", "INVOIC", "1.xml"), "x");
            File.WriteAllText(Path.Combine(root, "acme", "notes.txt"), "x"); // does not match the pattern
            var poller = new LocalDirectoryPoller(new NoopQueue(), NullLogger<LocalDirectoryPoller>.Instance,
                new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions
                {
                    Sources =
                    [
                        new FileSourceOptions { Name = "in", Path = root + Path.DirectorySeparatorChar, Pattern = "*.xml", Recursive = true },
                        new FileSourceOptions { Name = "missing", Path = Path.Combine(root, "nope") },
                    ]
                }));

            var listings = await poller.ListSourceFoldersAsync(CancellationToken.None);

            var ok = listings[0];
            Assert.Null(ok.Error);
            Assert.Equal("local", ok.Protocol);
            Assert.Equal(Path.DirectorySeparatorChar, ok.Separator);
            Assert.Equal(
                new[] { Path.Combine(root, "acme", "INVOIC"), Path.Combine(root, "acme", "INVOIC", "old"), Path.Combine(root, "acme", "ORDRSP") },
                ok.Folders.Order(StringComparer.Ordinal));
            Assert.NotNull(listings[1].Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDirectoryPoller_PatternThatThrows_IsAFailedListingNotAnException()
    {
        var root = Directory.CreateTempSubdirectory("fh-inventory-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            var poller = new LocalDirectoryPoller(new NoopQueue(), NullLogger<LocalDirectoryPoller>.Instance,
                new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions
                {
                    Sources = [new FileSourceOptions { Name = "bad", Path = root, Pattern = "bad\0name", Recursive = true }]
                }));

            var listing = Assert.Single(await poller.ListSourceFoldersAsync(CancellationToken.None));

            Assert.Equal("bad", listing.Source);
            Assert.NotNull(listing.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDirectoryPoller_Cancelled_ThrowsInsteadOfReturningAPartialListing()
    {
        var root = Directory.CreateTempSubdirectory("fh-inventory-").FullName;
        try
        {
            var poller = new LocalDirectoryPoller(new NoopQueue(), NullLogger<LocalDirectoryPoller>.Instance,
                new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions { Sources = [new FileSourceOptions { Name = "in", Path = root }] }));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.ListSourceFoldersAsync(new CancellationToken(canceled: true)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubPoller(IReadOnlyList<SourceFolderListing>? listings) : IFilePoller, ISourceFolderLister
    {
        public Task<Result> PollAsync(CancellationToken ct) => Task.FromResult(Result.Success());
        public Task<IReadOnlyList<SourceFolderListing>> ListSourceFoldersAsync(CancellationToken ct)
            => listings is null ? throw new InvalidOperationException("lister broke") : Task.FromResult(listings);
    }

    /// <summary>Waits for shutdown, then fails with a non-cancellation exception, as a client torn down mid-walk can.</summary>
    private sealed class BlockingLister : ISourceFolderLister
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<SourceFolderListing>> ListSourceFoldersAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("connection closed");
            }
            return [];
        }
    }

    private sealed class StubLister : ISourceFolderLister
    {
        public List<SourceFolderListing> Listings { get; } = [];
        public int Calls { get; private set; }
        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<SourceFolderListing>> ListSourceFoldersAsync(CancellationToken ct)
        {
            Calls++;
            FirstCall.TrySetResult();
            return Task.FromResult<IReadOnlyList<SourceFolderListing>>(Listings);
        }
    }

    private sealed class NoopQueue : IFileEventQueue
    {
        public Task<Result> EnqueueAsync(FileEvent fileEvent, CancellationToken ct) => Task.FromResult(Result.Success());
        public async IAsyncEnumerable<FileEvent> DequeueAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public IReadOnlyCollection<FileEvent> TryDrain(int maxCount) => [];
    }
}
