using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public class LocalFileTransferProcessorTests
{
    private sealed class FileTransferTestContext(LocalFileTransferProcessor processor, string sourceDir, string destDir, string filePath, bool createdDestDir) : IDisposable
    {
        public LocalFileTransferProcessor Processor { get; } = processor;
        public string SourceDir { get; } = sourceDir;
        public string DestinationDir { get; } = destDir;
        public string FilePath { get; } = filePath;
        private readonly bool _createdDestDir = createdDestDir;

        public void Dispose()
        {
            try { if (Directory.Exists(SourceDir)) Directory.Delete(SourceDir, true); } catch { }
            try { if (Directory.Exists(DestinationDir)) Directory.Delete(DestinationDir, true); } catch { }
        }
    }

    private static FileTransferTestContext CreateContext(bool move, bool createDestinationDir, bool enableTransfer, string? fileContent = null)
    {
        var srcDir = Path.Combine(Path.GetTempPath(), $"fh-src-{Guid.NewGuid():N}");
        var dstDir = Path.Combine(Path.GetTempPath(), $"fh-dst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        if (createDestinationDir)
        {
            Directory.CreateDirectory(dstDir);
        }
        var filePath = Path.Combine(srcDir, "sample.txt");
        File.WriteAllText(filePath, fileContent ?? "data");

        var featureOpts = new OptionsMonitorStub<PipelineFeaturesOptions>(new PipelineFeaturesOptions { EnableFileTransfer = enableTransfer });
        var sourcesOpts = new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions
        {
            Sources =
            [
                new() { Path = srcDir, MoveAfterProcessing = move, DestinationPath = dstDir, CreateDestinationDirectories = createDestinationDir }
            ]
        });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, featureOpts, sourcesOpts);
        return new FileTransferTestContext(processor, srcDir, dstDir, filePath, createDestinationDir);
    }

    [Fact]
    public async Task ProcessAsync_CopyMode_CopiesFile()
    {
        using var ctx = CreateContext(move: false, createDestinationDir: true, enableTransfer: true, fileContent: "hello");
        var fe = FileEventBuilder.FromPath(ctx.FilePath);

        var result = await ctx.Processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess);
        var destFile = Path.Combine(ctx.DestinationDir, Path.GetFileName(ctx.FilePath));
        Assert.True(File.Exists(destFile));
        Assert.True(File.Exists(ctx.FilePath)); // original remains
    }

    [Fact]
    public async Task ProcessAsync_MoveMode_MovesFile()
    {
        using var ctx = CreateContext(move: true, createDestinationDir: true, enableTransfer: true, fileContent: "hello");
        var fe = FileEventBuilder.FromPath(ctx.FilePath);

        var result = await ctx.Processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess);
        var destFile = Path.Combine(ctx.DestinationDir, Path.GetFileName(ctx.FilePath));
        Assert.True(File.Exists(destFile));
        Assert.False(File.Exists(ctx.FilePath)); // original moved
    }

    [Fact]
    public async Task ProcessAsync_DisabledFeature_DoesNothing()
    {
        using var ctx = CreateContext(move: true, createDestinationDir: true, enableTransfer: false, fileContent: "hello");
        var fe = FileEventBuilder.FromPath(ctx.FilePath);

        var result = await ctx.Processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(ctx.FilePath)); // unchanged
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
            Sources =
            [
                new() { Path = srcDirA, MoveAfterProcessing = false, DestinationPath = dstDirA },
                new() { Path = srcDirB, MoveAfterProcessing = true, DestinationPath = dstDirB }
            ]
        });
        var processor = new LocalFileTransferProcessor(NullLogger<LocalFileTransferProcessor>.Instance, featureOpts, sourcesOpts);

        var feA = FileEventBuilder.FromPath(fileA);
        var feB = FileEventBuilder.FromPath(fileB);
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
        using var ctx = CreateContext(move: false, createDestinationDir: false, enableTransfer: true, fileContent: "data");
        var fe = FileEventBuilder.FromPath(ctx.FilePath);

        var result = await ctx.Processor.ProcessAsync(fe, CancellationToken.None);
        Assert.True(result.IsSuccess); // skip still success result
        Assert.True(File.Exists(ctx.FilePath));
        Assert.False(Directory.Exists(ctx.DestinationDir)); // not created
    }
}
