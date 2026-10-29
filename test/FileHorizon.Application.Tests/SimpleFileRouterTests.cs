using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Models;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class SimpleFileRouterTests
{
    private static FileEvent MakeEvent(string protocol, string path)
    {
        var meta = new FileMetadata(path, 0, DateTimeOffset.UtcNow, "none", null);
        return new FileEvent(Guid.NewGuid().ToString("N"), meta, DateTimeOffset.UtcNow, protocol, "/dest", false);
    }

    private static IOptionsMonitor<RoutingOptions> Options(params RoutingRuleOptions[] rules)
        => new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { Rules = [.. rules] });

    [Fact]
    public async Task RouteAsync_NoMatch_ReturnsFailure()
    {
        var router = new SimpleFileRouter(
            routingOptions: Options(),
            destinationsOptions: new StaticOptionsMonitor<DestinationsOptions>(new DestinationsOptions()),
            logger: NullLogger<SimpleFileRouter>.Instance
        );
        var res = await router.RouteAsync(MakeEvent("local", "/data/file.txt"), CancellationToken.None);
        Assert.True(res.IsFailure);
    }

    [Fact]
    public async Task RouteAsync_MatchByProtocolAndGlob_ReturnsPlan()
    {
        var rule = new RoutingRuleOptions
        {
            Name = "csv-local",
            Protocol = "local",
            PathGlob = "**/*.csv",
            Destinations = new List<string> { "OutboxA" },
            Overwrite = true,
            RenamePattern = "{yyyyMMdd}-{fileName}"
        };
        var router = new SimpleFileRouter(
            routingOptions: Options(rule),
            destinationsOptions: new StaticOptionsMonitor<DestinationsOptions>(new DestinationsOptions()),
            logger: NullLogger<SimpleFileRouter>.Instance
        );
        var ev = MakeEvent("local", "/data/x/y/file.csv");
        var res = await router.RouteAsync(ev, CancellationToken.None);
        Assert.True(res.IsSuccess);
        var plan = Assert.Single(res.Value!);
        Assert.Equal("OutboxA", plan.DestinationName);
        Assert.Contains("-file.csv", plan.TargetPath);
        Assert.True(plan.Options.Overwrite);
    }

    [Fact]
    public async Task RouteAsync_Match_LogsRuleWithFileIdAndPath()
    {
        var logger = new CapturingLogger<SimpleFileRouter>();
        var router = new SimpleFileRouter(
            routingOptions: Options(new RoutingRuleOptions { Name = "ordrsp", PathGlob = "**/ORDRSP/*.xml", Destinations = ["OrdersBus"] }),
            destinationsOptions: new StaticOptionsMonitor<DestinationsOptions>(new DestinationsOptions()),
            logger: logger
        );
        var ev = MakeEvent("sftp", "sftp://host:22/partners/acme/ORDRSP/1.xml");

        await router.RouteAsync(ev, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("ordrsp", entry.State["Rule"]);
        Assert.Equal("OrdersBus", entry.State["Destination"]);
        Assert.Equal(ev.Id, entry.State["FileId"]);
        Assert.Equal("sftp://host:22/partners/acme/ORDRSP/1.xml", entry.State["Path"]);
    }
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    where T : class, new()
{
    private readonly T _value = value;
    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
