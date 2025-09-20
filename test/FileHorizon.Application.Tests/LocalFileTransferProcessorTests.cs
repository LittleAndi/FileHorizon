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

        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = true });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions { Sources = new List<FileSourceOptions> { new() { Path = srcDir, MoveAfterProcessing = false, DestinationPath = dstDir } } });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, featureOpts, sourcesOpts);
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

        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = true });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions { Sources = new List<FileSourceOptions> { new() { Path = srcDir, MoveAfterProcessing = true, DestinationPath = dstDir } } });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, featureOpts, sourcesOpts);
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

        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = false });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions { Sources = new List<FileSourceOptions> { new() { Path = srcDir, MoveAfterProcessing = true, DestinationPath = Path.Combine(Path.GetTempPath(), "fh-disabled-dst") } } });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, featureOpts, sourcesOpts);
        var fe = BuildEvent(filePath);

        var result = await processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(filePath)); // unchanged

        Directory.Delete(srcDir, true);
    }

    [Fact]
    public async Task ProcessAsync_PerSourceDestinations_AppliesCorrectTargets()
    {
        var srcDirA = Path.Combine(Path.GetTempPath(), "fh-srcA-" + Guid.NewGuid().ToString("N"));
        var srcDirB = Path.Combine(Path.GetTempPath(), "fh-srcB-" + Guid.NewGuid().ToString("N"));
        var dstDirA = Path.Combine(Path.GetTempPath(), "fh-dstA-" + Guid.NewGuid().ToString("N"));
        var dstDirB = Path.Combine(Path.GetTempPath(), "fh-dstB-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDirA);
        Directory.CreateDirectory(srcDirB);
        var fileA = Path.Combine(srcDirA, "a.txt");
        var fileB = Path.Combine(srcDirB, "b.txt");
        await File.WriteAllTextAsync(fileA, "A");
        await File.WriteAllTextAsync(fileB, "B");

        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = true });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions
        {
            Sources = new List<FileSourceOptions>
            {
                new() { Path = srcDirA, MoveAfterProcessing = false, DestinationPath = dstDirA },
                new() { Path = srcDirB, MoveAfterProcessing = true, DestinationPath = dstDirB }
            }
        });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, featureOpts, sourcesOpts);

        var feA = BuildEvent(fileA);
        var feB = BuildEvent(fileB);
        var r1 = await processor.ProcessAsync(feA, CancellationToken.None);
        var r2 = await processor.ProcessAsync(feB, CancellationToken.None);
        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);

        var destFileA = Path.Combine(dstDirA, Path.GetFileName(fileA));
        var destFileB = Path.Combine(dstDirB, Path.GetFileName(fileB));
        Assert.True(File.Exists(destFileA));
        Assert.True(File.Exists(destFileB));
        // A was copy, original remains
        Assert.True(File.Exists(fileA));
        // B was move, original removed
        Assert.False(File.Exists(fileB));

        Directory.Delete(srcDirA, true);
        Directory.Delete(srcDirB, true);
        Directory.Delete(dstDirA, true);
        Directory.Delete(dstDirB, true);
    }

    [Fact]
    public async Task ProcessAsync_NoCreateFlag_DestinationMissing_Skips()
    {
        var srcDir = Path.Combine(Path.GetTempPath(), "fh-nocreate-src-" + Guid.NewGuid().ToString("N"));
        var dstDir = Path.Combine(Path.GetTempPath(), "fh-nocreate-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        var filePath = Path.Combine(srcDir, "sample.txt");
        await File.WriteAllTextAsync(filePath, "data");

        // Intentionally do NOT create destination directory and set flag to false
        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = true });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions
        {
            Sources = new List<FileSourceOptions>
            {
                new() { Path = srcDir, MoveAfterProcessing = false, DestinationPath = dstDir, CreateDestinationDirectories = false }
            }
        });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, featureOpts, sourcesOpts);
        var fe = BuildEvent(filePath);

        var result = await processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess); // skip still success result
        Assert.True(File.Exists(filePath));
        Assert.False(Directory.Exists(dstDir)); // not created

        Directory.Delete(srcDir, true);
    }
}
