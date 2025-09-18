using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using FileHorizon.Application.Models;
using FileHorizon.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FileHorizon.Application.Tests;

public class LocalFileTransferProcessorTests
{
    private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T>
    {
        public OptionsMonitorStub(T value) => CurrentValue = value;
        public T CurrentValue { get; set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private static FileEvent BuildEvent(string path)
    {
        var fi = new FileInfo(path);
        var meta = new FileMetadata(fi.FullName, fi.Length, fi.LastWriteTimeUtc, "none", null);
        return new FileEvent(Guid.NewGuid().ToString("N"), meta, DateTimeOffset.UtcNow, "local", fi.FullName);
    }

    [Fact]
    public async Task ProcessAsync_CopyMode_CopiesFile()
    {
        var srcDir = Path.Combine(Path.GetTempPath(), "fh-copy-src-" + Guid.NewGuid().ToString("N"));
        var dstDir = Path.Combine(Path.GetTempPath(), "fh-copy-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        var filePath = Path.Combine(srcDir, "sample.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        var destOpts = new OptionsMonitorStub<FileDestinationOptions>(new FileDestinationOptions { RootPath = dstDir, CreateDirectories = true });
        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = true });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions { Sources = new List<FileSourceOptions> { new() { Path = srcDir, MoveAfterProcessing = false } } });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, destOpts, featureOpts, sourcesOpts);
        var fe = BuildEvent(filePath);

        var result = await processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess);
        var destFile = Path.Combine(dstDir, Path.GetFileName(filePath));
        Assert.True(File.Exists(destFile));
        Assert.True(File.Exists(filePath)); // original remains

        Directory.Delete(srcDir, true);
        Directory.Delete(dstDir, true);
    }

    [Fact]
    public async Task ProcessAsync_MoveMode_MovesFile()
    {
        var srcDir = Path.Combine(Path.GetTempPath(), "fh-move-src-" + Guid.NewGuid().ToString("N"));
        var dstDir = Path.Combine(Path.GetTempPath(), "fh-move-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        var filePath = Path.Combine(srcDir, "sample.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        var destOpts = new OptionsMonitorStub<FileDestinationOptions>(new FileDestinationOptions { RootPath = dstDir, CreateDirectories = true });
        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = true });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions { Sources = new List<FileSourceOptions> { new() { Path = srcDir, MoveAfterProcessing = true } } });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, destOpts, featureOpts, sourcesOpts);
        var fe = BuildEvent(filePath);

        var result = await processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess);
        var destFile = Path.Combine(dstDir, Path.GetFileName(filePath));
        Assert.True(File.Exists(destFile));
        Assert.False(File.Exists(filePath)); // original moved

        Directory.Delete(dstDir, true);
        // srcDir already missing file, but directory still there
        Directory.Delete(srcDir, true);
    }

    [Fact]
    public async Task ProcessAsync_DisabledFeature_DoesNothing()
    {
        var srcDir = Path.Combine(Path.GetTempPath(), "fh-disabled-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        var filePath = Path.Combine(srcDir, "sample.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        var destOpts = new OptionsMonitorStub<FileDestinationOptions>(new FileDestinationOptions { RootPath = Path.Combine(Path.GetTempPath(), "fh-disabled-dst") });
        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = false });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions { Sources = new List<FileSourceOptions> { new() { Path = srcDir, MoveAfterProcessing = true } } });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, destOpts, featureOpts, sourcesOpts);
        var fe = BuildEvent(filePath);

        var result = await processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(filePath)); // unchanged

        Directory.Delete(srcDir, true);
    }
}
