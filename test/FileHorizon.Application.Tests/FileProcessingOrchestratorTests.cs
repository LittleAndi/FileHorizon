using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class FileProcessingOrchestratorTests
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
    public async Task ProcessAsync_LocalToLocal_Succeeds()
    {
        var srcFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        var destRoot = Path.Combine(Path.GetTempPath(), "orchestrator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(srcFile)!);
        await File.WriteAllTextAsync(srcFile, "hello world");

        var ev = new FileEvent(
            Id: Guid.NewGuid().ToString("N"),
            Metadata: new FileMetadata(srcFile, new FileInfo(srcFile).Length, DateTimeOffset.UtcNow, "none", null),
            DiscoveredAtUtc: DateTimeOffset.UtcNow,
            Protocol: "local",
            DestinationPath: string.Empty);

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
        var router = new Infrastructure.Processing.SimpleFileRouter(new StaticOptionsMonitor<RoutingOptions>(routing), NullLogger<Infrastructure.Processing.SimpleFileRouter>.Instance);

        var readers = new List<IFileContentReader> { new Infrastructure.Processing.LocalFileContentReader(NullLogger<Infrastructure.Processing.LocalFileContentReader>.Instance) };
        var sinks = new List<IFileSink> { new Infrastructure.Processing.LocalFileSink(NullLogger<Infrastructure.Processing.LocalFileSink>.Instance) };
        var destinations = new DestinationsOptions
        {
            Local =
            [
                new LocalDestinationOptions { Name = "OutboxA", RootPath = destRoot }
            ]
        };
        var orchestrator = new FileProcessingOrchestrator(
            router,
            readers,
            sinks,
            new StaticOptionsMonitor<DestinationsOptions>(destinations),
            NullLogger<FileProcessingOrchestrator>.Instance);

        var res = await orchestrator.ProcessAsync(ev, CancellationToken.None);
        Assert.True(res.IsSuccess);

        var expected = Path.Combine(destinations.Local[0].RootPath, Path.GetFileName(srcFile));
        Assert.True(File.Exists(expected));

        // Cleanup
        try { File.Delete(srcFile); } catch { }
        try { Directory.Delete(destRoot, true); } catch { }
    }
}
