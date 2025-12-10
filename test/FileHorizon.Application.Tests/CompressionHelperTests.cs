using System.Text;
using FileHorizon.Application.Common;
using Xunit;

namespace FileHorizon.Application.Tests;

public class CompressionHelperTests
{
    [Fact]
    public async Task CompressAsync_WithValidData_ReturnsCompressedData()
    {
        // Use longer text to ensure compression actually reduces size (gzip has overhead for small data)
        var originalData = Encoding.UTF8.GetBytes(string.Join("", Enumerable.Repeat("This is test data that should be compressed using gzip compression algorithm. ", 20)));
        
        var compressed = await CompressionHelper.CompressAsync(originalData);
        
        Assert.NotNull(compressed);
        Assert.NotEmpty(compressed);
        // Compressed data should be smaller for text with repetition
        Assert.True(compressed.Length < originalData.Length, 
            $"Compressed size ({compressed.Length}) should be less than original size ({originalData.Length})");
    }

    [Fact]
    public async Task CompressAsync_WithEmptyData_ReturnsEmpty()
    {
        var emptyData = Array.Empty<byte>();
        
        var compressed = await CompressionHelper.CompressAsync(emptyData);
        
        Assert.NotNull(compressed);
        Assert.Empty(compressed);
    }

    [Fact]
    public async Task DecompressAsync_WithValidCompressedData_ReturnsOriginalData()
    {
        var originalData = Encoding.UTF8.GetBytes("This is test data for compression and decompression test.");
        var compressed = await CompressionHelper.CompressAsync(originalData);
        
        var decompressed = await CompressionHelper.DecompressAsync(compressed);
        
        Assert.Equal(originalData, decompressed);
    }

    [Fact]
    public async Task DecompressAsync_WithEmptyData_ReturnsEmpty()
    {
        var emptyData = Array.Empty<byte>();
        
        var decompressed = await CompressionHelper.DecompressAsync(emptyData);
        
        Assert.NotNull(decompressed);
        Assert.Empty(decompressed);
    }

    [Fact]
    public async Task CompressAndDecompress_RoundTrip_PreservesOriginalData()
    {
        var testCases = new[]
        {
            "Simple text",
            "Text with special characters: !@#$%^&*()",
            "Repeated text " + string.Join("", Enumerable.Repeat("repeat ", 100)),
            "Unicode text: ??????? ??",
            string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line {i} with some content"))
        };

        foreach (var testCase in testCases)
        {
            var originalData = Encoding.UTF8.GetBytes(testCase);
            
            var compressed = await CompressionHelper.CompressAsync(originalData);
            var decompressed = await CompressionHelper.DecompressAsync(compressed);
            var resultText = Encoding.UTF8.GetString(decompressed);
            
            Assert.Equal(testCase, resultText);
        }
    }

    [Fact]
    public async Task CompressAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var largeData = Encoding.UTF8.GetBytes(string.Join("", Enumerable.Repeat("test data ", 10000)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        // TaskCanceledException inherits from OperationCanceledException, so we accept both
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CompressionHelper.CompressAsync(largeData, cts.Token));
        
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task CompressAsync_WithRandomBinaryData_ProducesValidOutput()
    {
        // Random binary data typically doesn't compress well, may even grow
        var random = new Random(42); // Fixed seed for reproducibility
        var binaryData = new byte[1024];
        random.NextBytes(binaryData);
        
        var compressed = await CompressionHelper.CompressAsync(binaryData);
        
        Assert.NotNull(compressed);
        Assert.NotEmpty(compressed);
        
        // Verify it can be decompressed back to original
        var decompressed = await CompressionHelper.DecompressAsync(compressed);
        Assert.Equal(binaryData, decompressed);
    }

    [Fact]
    public async Task CompressAsync_WithAlreadyCompressedData_StillWorks()
    {
        // Compress data twice (though this would be inefficient in practice)
        var originalData = Encoding.UTF8.GetBytes("Test data for double compression");
        var firstCompression = await CompressionHelper.CompressAsync(originalData);
        
        // Compress the already-compressed data
        var secondCompression = await CompressionHelper.CompressAsync(firstCompression);
        
        Assert.NotNull(secondCompression);
        Assert.NotEmpty(secondCompression);
        // Second compression typically makes it larger (already compressed data doesn't compress well)
        Assert.True(secondCompression.Length >= firstCompression.Length);
        
        // Verify double decompression works
        var firstDecompression = await CompressionHelper.DecompressAsync(secondCompression);
        var secondDecompression = await CompressionHelper.DecompressAsync(firstDecompression);
        Assert.Equal(originalData, secondDecompression);
    }

    [Fact]
    public async Task CompressAsync_WithSmallData_MayIncreaseSizeButStillValid()
    {
        // Very small data often becomes larger due to gzip header overhead
        var smallData = Encoding.UTF8.GetBytes("Hi");
        
        var compressed = await CompressionHelper.CompressAsync(smallData);
        
        Assert.NotNull(compressed);
        Assert.NotEmpty(compressed);
        // Gzip header is ~18 bytes, so very small data will be larger when compressed
        // But it should still decompress correctly
        var decompressed = await CompressionHelper.DecompressAsync(compressed);
        Assert.Equal(smallData, decompressed);
    }
}
