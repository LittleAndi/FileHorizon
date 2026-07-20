using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Common.Telemetry;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace FileHorizon.Application.Infrastructure.Processing;

/// <summary>
/// Sink writing files to Azure Blob Storage. Resolves the destination options by logical name
/// (carried on <see cref="FileReference.SourceName"/>), composes the blob path from the configured
/// prefix, determines content type per strategy and delegates the upload to <see cref="IBlobStorageClient"/>.
/// </summary>
public sealed class AzureBlobFileSink(
    ILogger<AzureBlobFileSink> logger,
    IBlobStorageClient blobClient,
    IOptionsMonitor<DestinationsOptions> destinations,
    IFileTypeDetector fileTypeDetector) : IFileSink
{
    public const string SinkName = "AzureBlob";
    public const string Scheme = "azure-blob";

    private readonly ILogger<AzureBlobFileSink> _logger = logger;
    private readonly IBlobStorageClient _blobClient = blobClient;
    private readonly IOptionsMonitor<DestinationsOptions> _destinations = destinations;
    private readonly IFileTypeDetector _fileTypeDetector = fileTypeDetector;

    public string Name => SinkName;

    public async Task<Result> WriteAsync(FileReference target, Stream content, FileWriteOptions options, CancellationToken ct)
    {
        if (!string.Equals(target.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(Error.Validation.Invalid($"AzureBlobFileSink received non-blob scheme '{target.Scheme}'"));
        }
        var destination = _destinations.CurrentValue.AzureBlob
            .FirstOrDefault(d => string.Equals(d.Name, target.SourceName, StringComparison.OrdinalIgnoreCase));
        if (destination is null)
        {
            return Result.Failure(Error.Storage.NotConfigured(target.SourceName ?? "<unknown>"));
        }

        var blobPath = ComposeBlobPath(destination.RootPathPrefix, target.Path);
        var contentType = ResolveContentType(destination, blobPath);
        var overwrite = destination.OverwritePolicy switch
        {
            BlobOverwritePolicy.Overwrite => true,
            BlobOverwritePolicy.FailIfExists => false,
            _ => options?.Overwrite == true
        };

        using var activity = TelemetryInstrumentation.ActivitySource.StartActivity("sink.write", ActivityKind.Internal);
        activity?.SetTag("sink.name", Name);
        activity?.SetTag("blob.container", destination.ContainerName);
        activity?.SetTag("blob.path", blobPath);
        activity?.SetTag("blob.tier", destination.AccessTier);

        var request = new BlobUploadRequest(
            DestinationName: destination.Name,
            ContainerName: destination.ContainerName,
            BlobPath: blobPath,
            ContentType: contentType,
            Overwrite: overwrite,
            AccessTier: destination.AccessTier);
        var upload = await _blobClient.UploadAsync(request, content, ct).ConfigureAwait(false);
        if (upload.IsFailure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, upload.Error.Code);
            return Result.Failure(upload.Error);
        }
        var result = upload.Value!;
        activity?.SetTag("blob.account", result.AccountName);
        activity?.SetStatus(ActivityStatusCode.Ok);
        TelemetryInstrumentation.BytesCopied.Add(result.BytesWritten);
        _logger.LogInformation("Wrote blob {BlobUri} ({Bytes} bytes) for destination {Destination}",
            result.BlobUri, result.BytesWritten, destination.Name);
        return Result.Success();
    }

    private string? ResolveContentType(AzureBlobDestinationOptions destination, string blobPath) =>
        destination.ContentTypeStrategy switch
        {
            BlobContentTypeStrategy.Provided => destination.ContentType,
            BlobContentTypeStrategy.None => null,
            _ => _fileTypeDetector.Detect(blobPath) ?? "application/octet-stream"
        };

    /// <summary>Joins the configured prefix and target path into a normalized, relative blob path.</summary>
    internal static string ComposeBlobPath(string? prefix, string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(prefix)) return normalized;
        var normalizedPrefix = prefix.Replace('\\', '/').Trim('/');
        return normalizedPrefix.Length == 0 ? normalized : $"{normalizedPrefix}/{normalized}";
    }
}
