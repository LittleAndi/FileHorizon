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
            new RoutingRuleOptions { Name = "invoic", Protocol = "sftp", PathRegex = "/INVOIC/", Destinations = ["Archive"] },
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
