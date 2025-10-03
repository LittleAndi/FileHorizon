using System.Text;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileHorizon.Application.Tests;

public class LocalFileContentReaderTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileHorizonTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsStream_ForExistingFile()
    {
        var dir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(dir, "test.txt");
            var content = Encoding.UTF8.GetBytes("hello");
            await File.WriteAllBytesAsync(filePath, content);

            var reader = new LocalFileContentReader(NullLogger<LocalFileContentReader>.Instance);
            var res = await reader.OpenReadAsync(new FileReference("local", null, null, filePath, "InboxA"), CancellationToken.None);

            Assert.True(res.IsSuccess);
            await using var stream = res.Value!;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(content, ms.ToArray());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetAttributesAsync_ReturnsInfo_ForExistingFile()
    {
        var dir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(dir, "attr.txt");
            var content = new byte[123];
            new Random().NextBytes(content);
            await File.WriteAllBytesAsync(filePath, content);

            var reader = new LocalFileContentReader(NullLogger<LocalFileContentReader>.Instance);
            var res = await reader.GetAttributesAsync(new FileReference("local", null, null, filePath, "InboxA"), CancellationToken.None);

            Assert.True(res.IsSuccess);
            Assert.Equal(123, res.Value!.Size);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task OpenReadAsync_Fails_ForNonLocalScheme()
    {
        var reader = new LocalFileContentReader(NullLogger<LocalFileContentReader>.Instance);
        var res = await reader.OpenReadAsync(new FileReference("sftp", "host", 22, "/file.txt", "Src"), CancellationToken.None);
        Assert.True(res.IsFailure);
    }

    [Fact]
    public async Task GetAttributesAsync_Fails_ForMissingFile()
    {
        var reader = new LocalFileContentReader(NullLogger<LocalFileContentReader>.Instance);
        var res = await reader.GetAttributesAsync(new FileReference("local", null, null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt"), "Src"), CancellationToken.None);
        Assert.True(res.IsFailure);
    }
}
