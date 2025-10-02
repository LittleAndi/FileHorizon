using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class SimpleFileRouterTests
{
    private static FileEvent MakeEvent(string protocol, string path)
    {
        var meta = new FileMetadata(path, 0, DateTimeOffset.UtcNow, "none", null);
        return new FileEvent(Guid.NewGuid().ToString("N"), meta, DateTimeOffset.UtcNow, protocol, "/dest");
    }

    private static IOptionsMonitor<RoutingOptions> Options(params RoutingRuleOptions[] rules)
        => new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { Rules = rules.ToList() });

    [Fact]
    public async Task RouteAsync_NoMatch_ReturnsFailure()
    {
        var router = new SimpleFileRouter(Options(), NullLogger<SimpleFileRouter>.Instance);
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
        var router = new SimpleFileRouter(Options(rule), NullLogger<SimpleFileRouter>.Instance);
        var ev = MakeEvent("local", "/data/x/y/file.csv");
        var res = await router.RouteAsync(ev, CancellationToken.None);
        Assert.True(res.IsSuccess);
        var plan = Assert.Single(res.Value!);
        Assert.Equal("OutboxA", plan.DestinationName);
        Assert.Contains("-file.csv", plan.TargetPath);
        Assert.True(plan.Options.Overwrite);
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
