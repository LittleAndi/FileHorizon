using FileHorizon.Application.Configuration;
using FileHorizon.Application.Core;
using FileHorizon.Application.Models;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace FileHorizon.Application.Tests;

public class FileProcessingServiceOrchestratedFlowTests
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

    [Fact]
    public async Task HandleAsync_Local_To_Local_EndToEnd_Succeeds_With_Orchestrator()
    {
        // Arrange DI
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug));
        services.AddApplicationServices();
        services.AddSingleton<IFileContentPublisher, TestNoopFileContentPublisher>();

        // Required base options
        services.AddSingleton<IOptions<PollingOptions>>(Options.Create(new PollingOptions()));
        services.AddSingleton<IOptions<RedisOptions>>(Options.Create(new RedisOptions { Enabled = false }));
        services.AddSingleton<IOptions<FileSourcesOptions>>(Options.Create(new FileSourcesOptions()));
        services.AddSingleton<IOptions<PipelineOptions>>(Options.Create(new PipelineOptions()));
        services.AddSingleton<IOptions<IdempotencyOptions>>(Options.Create(new IdempotencyOptions { Enabled = false }));

        // Feature flags (orchestrator always enabled by default now)
        services.AddSingleton<IOptions<PipelineFeaturesOptions>>(Options.Create(new PipelineFeaturesOptions
        {
            EnableLocalPoller = true
        }));

        // Routing and destinations for test
        var destRoot = Path.Combine(Path.GetTempPath(), "orchestrated-service-tests", Guid.NewGuid().ToString("N"));
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
                    Overwrite = true,
                    RenamePattern = "{fileName}"
                }
            ]
        };
        services.AddSingleton<IOptionsMonitor<RoutingOptions>>(new StaticOptionsMonitor<RoutingOptions>(routing));
        var destinations = new DestinationsOptions
        {
            Local = [new LocalDestinationOptions { Name = "OutboxA", RootPath = destRoot }]
        };
        services.AddSingleton<IOptionsMonitor<DestinationsOptions>>(new StaticOptionsMonitor<DestinationsOptions>(destinations));

        using var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<IFileProcessingService>();

        // Create source file
        var srcFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(srcFile)!);
        await File.WriteAllTextAsync(srcFile, "hello orchestrator");

        var ev = new FileEvent(
            Id: Guid.NewGuid().ToString("N"),
            Metadata: new FileMetadata(srcFile, new FileInfo(srcFile).Length, DateTimeOffset.UtcNow, "none", null),
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: "local",
            DestinationPath: string.Empty,
            DeleteAfterTransfer: false);

        // Act
        var result = await svc.HandleAsync(ev, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var expected = Path.Combine(destRoot, Path.GetFileName(srcFile));
        Assert.True(File.Exists(expected));

        // Cleanup
        try { File.Delete(srcFile); } catch { }
        try { Directory.Delete(destRoot, true); } catch { }
    }

    private sealed class TestNoopFileContentPublisher : IFileContentPublisher
    {
        public Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct) => Task.FromResult(Result.Success());
    }
}
