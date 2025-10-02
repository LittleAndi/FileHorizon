using System.Text;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public class LocalFileSinkTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileHorizonTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task WriteAsync_HandlesLargeStream()
    {
        var dir = CreateTempDir();
        try
        {
            var dest = Path.Combine(dir, "large", "big.bin");
            var sink = new LocalFileSink(NullLogger<LocalFileSink>.Instance);
            var data = new byte[5 * 1024 * 1024]; // 5 MB
            new Random(42).NextBytes(data); // Devskim: ignore DS148264 - Test code
            await using var input = new MemoryStream(data);
            var res = await sink.WriteAsync(new FileReference("local", null, null, dest, "Dest"), input, new FileWriteOptions(true, false, null), CancellationToken.None);
            Assert.True(res.IsSuccess);
            Assert.True(File.Exists(dest));
            var writtenLen = new FileInfo(dest).Length;
            Assert.Equal(data.LongLength, writtenLen);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task WriteAsync_WritesFile_ToDestination()
    {
        var dir = CreateTempDir();
        try
        {
            var dest = Path.Combine(dir, "out", "a.txt");
            var sink = new LocalFileSink(NullLogger<LocalFileSink>.Instance);
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
            var res = await sink.WriteAsync(new FileReference("local", null, null, dest, "Dest"), input, new FileWriteOptions(true, false, null), CancellationToken.None);
            Assert.True(res.IsSuccess);
            Assert.True(File.Exists(dest));
            var text = await File.ReadAllTextAsync(dest);
            Assert.Equal("hello", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task WriteAsync_RespectsOverwriteFalse()
    {
        var dir = CreateTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.txt");
            await File.WriteAllTextAsync(dest, "existing");
            var sink = new LocalFileSink(NullLogger<LocalFileSink>.Instance);
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes("new"));
            var res = await sink.WriteAsync(new FileReference("local", null, null, dest, "Dest"), input, new FileWriteOptions(false, false, null), CancellationToken.None);
            Assert.True(res.IsFailure);
            Assert.Equal("existing", await File.ReadAllTextAsync(dest));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task WriteAsync_AppliesRenamePattern()
    {
        var dir = CreateTempDir();
        try
        {
            var dest = Path.Combine(dir, "nested", "name.txt");
            var sink = new LocalFileSink(NullLogger<LocalFileSink>.Instance);
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes("x"));
            var res = await sink.WriteAsync(new FileReference("local", null, null, dest, "Dest"), input, new FileWriteOptions(true, false, "{yyyyMMdd}-{fileName}"), CancellationToken.None);
            Assert.True(res.IsSuccess);
            var expectedPrefix = DateTimeOffset.UtcNow.ToString("yyyyMMdd") + "-";
            var dirPath = Path.GetDirectoryName(dest)!;
            var files = Directory.GetFiles(dirPath);
            Assert.Contains(files, f => Path.GetFileName(f).StartsWith(expectedPrefix));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
