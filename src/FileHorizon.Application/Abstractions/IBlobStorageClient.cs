using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Abstractions;

/// <summary>
/// Minimal blob storage operations used by the blob sink. Abstracted so the sink can be unit tested
/// without a live storage account.
/// </summary>
public interface IBlobStorageClient
{
    Task<Result<BlobUploadResult>> UploadAsync(BlobUploadRequest request, Stream content, CancellationToken ct);
}
