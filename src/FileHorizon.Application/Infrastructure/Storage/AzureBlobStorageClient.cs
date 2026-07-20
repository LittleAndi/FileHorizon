using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Common.Telemetry;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FileHorizon.Application.Infrastructure.Storage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IBlobStorageClient"/>. Streams content to block blobs
/// (no full buffering) and relies on the Azure SDK retry policy for transient failures, configured per
/// destination via <see cref="AzureBlobTechnicalOptions"/>.
/// </summary>
public sealed class AzureBlobStorageClient(
    ILogger<AzureBlobStorageClient> logger,
    IOptionsMonitor<DestinationsOptions>? destinations = null) : IBlobStorageClient
{
    private readonly ILogger<AzureBlobStorageClient> _logger = logger;
    private readonly IOptionsMonitor<DestinationsOptions>? _destinations = destinations; // may be null in isolated unit tests
    private readonly ConcurrentDictionary<string, BlobServiceClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Counter<long> _uploadBytes = TelemetryInstrumentation.Meter.CreateCounter<long>("blob.upload.bytes", unit: "bytes", description: "Total bytes uploaded to blob storage");
    private static readonly Counter<long> _uploadSuccesses = TelemetryInstrumentation.Meter.CreateCounter<long>("blob.upload.successes", description: "Number of successful blob uploads");
    private static readonly Counter<long> _uploadFailures = TelemetryInstrumentation.Meter.CreateCounter<long>("blob.upload.failures", description: "Number of failed blob uploads");
    private static readonly Histogram<double> _uploadDurationMs = TelemetryInstrumentation.Meter.CreateHistogram<double>("blob.upload.duration.ms", unit: "ms", description: "Blob upload duration in milliseconds");

    public async Task<Result<BlobUploadResult>> UploadAsync(BlobUploadRequest request, Stream content, CancellationToken ct)
    {
        var destination = _destinations?.CurrentValue.AzureBlob
            .FirstOrDefault(d => string.Equals(d.Name, request.DestinationName, StringComparison.OrdinalIgnoreCase));
        if (destination is null)
        {
            return Result<BlobUploadResult>.Failure(Error.Storage.NotConfigured(request.DestinationName));
        }
        var tech = destination.BlobTechnical ?? new AzureBlobTechnicalOptions();
        var service = GetOrCreateClient(tech);
        if (service is null)
        {
            return Result<BlobUploadResult>.Failure(Error.Storage.NotConfigured(
                $"{request.DestinationName} (no ConnectionString, AccountName or ServiceUri)"));
        }

        var container = service.GetBlobContainerClient(request.ContainerName);
        var blob = container.GetBlobClient(request.BlobPath);
        var uploadOptions = new BlobUploadOptions();
        if (!string.IsNullOrWhiteSpace(request.ContentType))
        {
            uploadOptions.HttpHeaders = new BlobHttpHeaders { ContentType = request.ContentType };
        }
        if (!string.IsNullOrWhiteSpace(request.AccessTier))
        {
            uploadOptions.AccessTier = new AccessTier(request.AccessTier);
        }
        if (!request.Overwrite)
        {
            // If-None-Match: * makes the service reject the upload when the blob already exists.
            uploadOptions.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
        }

        var destinationTag = new KeyValuePair<string, object?>("destination", request.DestinationName);
        var sw = Stopwatch.StartNew();
        try
        {
            var counting = new CountingReadStream(content);
            await blob.UploadAsync(counting, uploadOptions, ct).ConfigureAwait(false);
            sw.Stop();
            _uploadBytes.Add(counting.BytesRead, destinationTag);
            _uploadSuccesses.Add(1, destinationTag, new KeyValuePair<string, object?>("tier", request.AccessTier ?? "default"));
            _uploadDurationMs.Record(sw.Elapsed.TotalMilliseconds, destinationTag);
            _logger.LogDebug("Uploaded blob {Container}/{Path} ({Bytes} bytes) to destination {Destination}",
                request.ContainerName, request.BlobPath, counting.BytesRead, request.DestinationName);
            return Result<BlobUploadResult>.Success(new BlobUploadResult(
                AccountName: blob.AccountName,
                ContainerName: request.ContainerName,
                BlobPath: request.BlobPath,
                BlobUri: blob.Uri,
                BytesWritten: counting.BytesRead));
        }
        catch (RequestFailedException rfe)
        {
            sw.Stop();
            var error = MapError(rfe, request);
            _uploadFailures.Add(1, destinationTag, new KeyValuePair<string, object?>("reason", error.Code));
            _logger.LogError(rfe, "Failed uploading blob {Container}/{Path} to destination {Destination}: {Code}",
                request.ContainerName, request.BlobPath, request.DestinationName, error.Code);
            return Result<BlobUploadResult>.Failure(error);
        }
        catch (Azure.Identity.AuthenticationFailedException afe)
        {
            sw.Stop();
            _uploadFailures.Add(1, destinationTag, new KeyValuePair<string, object?>("reason", "Storage.Authorization"));
            _logger.LogError(afe, "Authentication failed uploading blob {Container}/{Path} to destination {Destination}",
                request.ContainerName, request.BlobPath, request.DestinationName);
            return Result<BlobUploadResult>.Failure(Error.Storage.Authorization(FirstLine(afe.Message)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _uploadFailures.Add(1, destinationTag, new KeyValuePair<string, object?>("reason", "Storage.UploadError"));
            _logger.LogError(ex, "Unexpected error uploading blob {Container}/{Path} to destination {Destination}",
                request.ContainerName, request.BlobPath, request.DestinationName);
            return Result<BlobUploadResult>.Failure(Error.Storage.UploadError(FirstLine(ex.Message)));
        }
    }

    /// <summary>Translates Azure SDK request failures into domain errors, classifying transient vs permanent.</summary>
    internal static Error MapError(RequestFailedException ex, BlobUploadRequest request)
    {
        if (string.Equals(ex.ErrorCode, BlobErrorCode.BlobAlreadyExists.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(ex.ErrorCode, BlobErrorCode.ConditionNotMet.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Storage.AlreadyExists($"{request.ContainerName}/{request.BlobPath}");
        }
        if (string.Equals(ex.ErrorCode, BlobErrorCode.ContainerNotFound.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Storage.ContainerMissing(request.ContainerName);
        }
        return ex.Status switch
        {
            401 or 403 => Error.Storage.Authorization(FirstLine(ex.Message)),
            408 or 429 or 500 or 502 or 503 or 504 => Error.Storage.UploadTransient(FirstLine(ex.Message)),
            _ => Error.Storage.UploadError(FirstLine(ex.Message))
        };
    }

    // Error messages are trimmed to their first line so response headers/URLs never leak into logs or results.
    private static string FirstLine(string message)
    {
        var idx = message.IndexOfAny(['\r', '\n']);
        return idx < 0 ? message : message[..idx];
    }

    private BlobServiceClient? GetOrCreateClient(AzureBlobTechnicalOptions tech)
    {
        // Prefer connection string
        if (!string.IsNullOrWhiteSpace(tech.ConnectionString))
        {
            return _clients.GetOrAdd("cs:" + tech.ConnectionString, _ => new BlobServiceClient(tech.ConnectionString, CreateClientOptions(tech)));
        }
        // Managed identity path via explicit service URI or account name
        var serviceUri = !string.IsNullOrWhiteSpace(tech.ServiceUri)
            ? tech.ServiceUri
            : !string.IsNullOrWhiteSpace(tech.AccountName)
                ? $"https://{tech.AccountName}.blob.core.windows.net"
                : null;
        if (serviceUri is null) return null;
        return _clients.GetOrAdd("mi:" + serviceUri, _ =>
        {
            Azure.Core.TokenCredential credential = string.IsNullOrWhiteSpace(tech.ManagedIdentityClientId)
                ? new Azure.Identity.DefaultAzureCredential()
                : new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions { ManagedIdentityClientId = tech.ManagedIdentityClientId });
            return new BlobServiceClient(new Uri(serviceUri), credential, CreateClientOptions(tech));
        });
    }

    private static BlobClientOptions CreateClientOptions(AzureBlobTechnicalOptions tech)
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = Math.Max(0, tech.MaxRetries);
        options.Retry.Delay = TimeSpan.FromMilliseconds(Math.Max(0, tech.RetryBaseDelayMs));
        options.Retry.MaxDelay = TimeSpan.FromMilliseconds(Math.Max(tech.RetryBaseDelayMs, tech.RetryMaxDelayMs));
        options.Retry.Mode = Azure.Core.RetryMode.Exponential;
        return options;
    }

    /// <summary>Counts bytes read so upload size can be reported without requiring a seekable stream. Does not own the inner stream.</summary>
    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
