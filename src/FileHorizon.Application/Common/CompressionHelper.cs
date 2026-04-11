using System.IO.Compression;

namespace FileHorizon.Application.Common;

/// <summary>
/// Provides gzip compression utilities for message payloads.
/// </summary>
public static class CompressionHelper
{
    /// <summary>
    /// Compresses the input data using gzip compression.
    /// </summary>
    /// <param name="data">The data to compress.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compressed data as a byte array.</returns>
    public static async Task<byte[]> CompressAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        using var outputStream = new MemoryStream();
        await using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzipStream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        return outputStream.ToArray();
    }

    /// <summary>
    /// Decompresses gzip-compressed data.
    /// </summary>
    /// <param name="compressedData">The compressed data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Decompressed data as a byte array.</returns>
    public static async Task<byte[]> DecompressAsync(ReadOnlyMemory<byte> compressedData, CancellationToken ct = default)
    {
        if (compressedData.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        // Optimize memory usage: avoid array copy when the underlying array is accessible
        MemoryStream inputStream;
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(compressedData, out var segment))
        {
            // Use the underlying array directly without copying
            inputStream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
        }
        else
        {
            // Fallback: copy to array (needed for non-array-backed memory)
            inputStream = new MemoryStream(compressedData.ToArray());
        }

        using (inputStream)
        {
            await using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();
            await gzipStream.CopyToAsync(outputStream, ct).ConfigureAwait(false);
            return outputStream.ToArray();
        }
    }
}
