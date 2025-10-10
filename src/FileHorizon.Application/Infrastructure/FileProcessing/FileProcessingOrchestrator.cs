using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Common.Telemetry;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Remote;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace FileHorizon.Application.Infrastructure.FileProcessing;

/// <summary>
/// Orchestrated processor: routes incoming file events to one or more destinations, reads content via protocol-specific reader,
/// and writes via destination sink. Initial implementation handles a single local destination.
/// </summary>
public sealed class FileProcessingOrchestrator(
    IFileRouter router,
    IEnumerable<IFileContentReader> readers,
    IEnumerable<IFileSink> sinks,
    IOptionsMonitor<DestinationsOptions> destinations,
    IOptionsMonitor<IdempotencyOptions> idempotencyOptions,
    IOptionsMonitor<RemoteFileSourcesOptions> remoteSources,
    IIdempotencyStore idempotencyStore,
    ISftpClientFactory sftpFactory,
    ISecretResolver secretResolver,
    ILogger<SftpRemoteFileClient> sftpClientLogger,
    ILogger<FtpRemoteFileClient> ftpClientLogger,
    IFileContentPublisher publisher,
    IFileTypeDetector fileTypeDetector,
    ILogger<FileProcessingOrchestrator> logger) : IFileProcessor
{
    private readonly IFileRouter _router = router;
    private readonly IEnumerable<IFileContentReader> _readers = readers;
    private readonly IEnumerable<IFileSink> _sinks = sinks;
    private readonly IOptionsMonitor<DestinationsOptions> _destinations = destinations;
    private readonly IOptionsMonitor<IdempotencyOptions> _idempotencyOptions = idempotencyOptions;
    private readonly IIdempotencyStore _idempotencyStore = idempotencyStore;
    private readonly IOptionsMonitor<RemoteFileSourcesOptions> _remoteSources = remoteSources;
    private readonly ISftpClientFactory _sftpFactory = sftpFactory;
    private readonly ISecretResolver _secretResolver = secretResolver;
    private readonly ILogger<SftpRemoteFileClient> sftpClientLogger = sftpClientLogger;
    private readonly ILogger<FtpRemoteFileClient> ftpClientLogger = ftpClientLogger;
    private readonly ILogger<FileProcessingOrchestrator> _logger = logger;
    private readonly IFileContentPublisher _publisher = publisher;
    private readonly IFileTypeDetector _fileTypeDetector = fileTypeDetector;

    public async Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct)
    {
        using var activity = TelemetryInstrumentation.ActivitySource.StartActivity("file.orchestrate", ActivityKind.Internal);
        activity?.SetTag("file.protocol", fileEvent.Protocol);
        activity?.SetTag("file.source_path", fileEvent.Metadata.SourcePath);

        // Idempotency check (optional)
        var idempotencyGate = await EnsureFirstProcessingAsync(fileEvent, ct).ConfigureAwait(false);
        if (idempotencyGate.IsFailure || !idempotencyGate.Value)
        {
            return idempotencyGate.IsFailure ? Result.Failure(idempotencyGate.Error) : Result.Success();
        }

        // Route to destinations (currently handle single destination only)
        var planResult = await GetFirstDestinationPlanAsync(fileEvent, ct).ConfigureAwait(false);
        if (planResult.IsFailure)
        {
            return Result.Failure(planResult.Error);
        }
        if (planResult.Value is null)
        {
            return Result.Success(); // nothing to do
        }
        var plan = planResult.Value;

        // Open source stream via helper
        var open = await OpenSourceStreamAsync(fileEvent, ct).ConfigureAwait(false);
        if (open.IsFailure)
        {
            return Result.Failure(open.Error);
        }
        await using var stream = open.Value!;

        var dispatchResult = await DispatchDestinationAsync(fileEvent, plan, stream, ct).ConfigureAwait(false);
        return dispatchResult;
    }

    private async Task<Result> ProcessServiceBusAsync(FileEvent fileEvent, DestinationPlan plan, Stream stream, CancellationToken ct)
    {
        using var publishActivity = TelemetryInstrumentation.ActivitySource.StartActivity("servicebus.publish", ActivityKind.Producer);
        publishActivity?.SetTag("messaging.system", "azure.servicebus");
        publishActivity?.SetTag("messaging.destination", plan.DestinationName);
        // Read entire content as raw bytes (binary-safe). We also take a prefix sample for content sniffing.
        byte[] contentBytes;
        byte[] sampleBuffer;
        const int sampleSize = 4096; // align with ContentDetectionOptions default
        using (var ms = new MemoryStream())
        {
            if (stream.CanSeek)
            {
                var originalPos = stream.Position;
                var toRead = (int)Math.Min(sampleSize, stream.Length - stream.Position);
                sampleBuffer = new byte[toRead];
                var read = await stream.ReadAsync(sampleBuffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read < toRead)
                {
                    Array.Resize(ref sampleBuffer, read);
                }
                stream.Position = originalPos; // rewind
            }
            else
            {
                sampleBuffer = Array.Empty<byte>();
            }
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            contentBytes = ms.ToArray();
            if (sampleBuffer.Length == 0)
            {
                var altLen = Math.Min(sampleSize, contentBytes.Length);
                sampleBuffer = contentBytes.AsSpan(0, altLen).ToArray();
            }
        }

        string? configuredContentType = _destinations.CurrentValue.ServiceBus
            .FirstOrDefault(x => string.Equals(x.Name, plan.DestinationName, StringComparison.OrdinalIgnoreCase))?
            .ContentType;
        var detectedContentType = _fileTypeDetector.Detect(fileEvent.Metadata.SourcePath, sampleBuffer);
        var finalContentType = configuredContentType ?? detectedContentType ?? "application/octet-stream";
        publishActivity?.SetTag("messaging.content_type", finalContentType);
        var request = new FilePublishRequest(
            SourcePath: fileEvent.Metadata.SourcePath,
            FileName: Path.GetFileName(fileEvent.Metadata.SourcePath),
            Content: contentBytes,
            ContentType: finalContentType,
            DestinationName: plan.DestinationName,
            IsTopic: plan.IsTopic,
            ApplicationProperties: new Dictionary<string, string>
            {
                ["fh.fileId"] = fileEvent.Id,
                ["fh.protocol"] = fileEvent.Protocol
            }
        );
        var publish = await _publisher.PublishAsync(request, ct).ConfigureAwait(false);
        if (publish.IsFailure)
        {
            publishActivity?.SetStatus(ActivityStatusCode.Error, publish.Error.ToString());
            return publish; // propagate failure
        }
        publishActivity?.SetStatus(ActivityStatusCode.Ok);

        await stream.DisposeAsync().ConfigureAwait(false);
        await DeleteSourceIfRequestedAsync(fileEvent, ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result> ProcessLocalAsync(FileEvent fileEvent, DestinationPlan plan, Stream stream, CancellationToken ct)
    {
        var destRoot = ResolveLocalDestinationRoot(plan.DestinationName);
        if (destRoot is null)
        {
            _logger.LogWarning("Unknown local destination {Dest}", plan.DestinationName);
            return Result.Failure(Error.Validation.Invalid($"Unknown destination '{plan.DestinationName}'"));
        }
        var targetRef = new FileReference(
            Scheme: "local",
            Host: null,
            Port: null,
            Path: System.IO.Path.Combine(destRoot, plan.TargetPath),
            SourceName: plan.DestinationName);
        var sink = _sinks.FirstOrDefault(s => string.Equals(s.Name, "Local", StringComparison.OrdinalIgnoreCase));
        if (sink is null)
        {
            return Result.Failure(Error.Unspecified("Sink.LocalMissing", "Local sink not registered"));
        }
        var write = await sink.WriteAsync(targetRef, stream, plan.Options, ct).ConfigureAwait(false);
        if (write.IsFailure)
        {
            return write;
        }
        await stream.DisposeAsync().ConfigureAwait(false);
        await DeleteSourceIfRequestedAsync(fileEvent, ct).ConfigureAwait(false);
        return Result.Success();
    }

    private Task<Result> DispatchDestinationAsync(FileEvent fileEvent, DestinationPlan plan, Stream stream, CancellationToken ct) =>
        plan.Kind switch
        {
            Models.DestinationKind.ServiceBus => ProcessServiceBusAsync(fileEvent, plan, stream, ct),
            Models.DestinationKind.Local => ProcessLocalAsync(fileEvent, plan, stream, ct),
            Models.DestinationKind.Sftp => Task.FromResult(Result.Failure(Error.Unspecified("Destination.SftpUnsupported", "Sftp destination handling not implemented"))),
            _ => Task.FromResult(Result.Failure(Error.Unspecified("Destination.UnknownKind", $"Unhandled destination kind {plan.Kind}")))
        };

    /// <summary>
    /// Selects appropriate reader for the event protocol and opens the source stream.
    /// </summary>
    private async Task<Result<Stream>> OpenSourceStreamAsync(FileEvent fileEvent, CancellationToken ct)
    {
        var reader = SelectReader(fileEvent.Protocol);
        if (reader is null)
        {
            return Result<Stream>.Failure(Error.Validation.Invalid($"No reader for protocol '{fileEvent.Protocol}'"));
        }

        var sourceRef = new FileReference(
            Scheme: fileEvent.Protocol,
            Host: null,
            Port: null,
            Path: fileEvent.Metadata.SourcePath,
            SourceName: null);

        using var readActivity = TelemetryInstrumentation.ActivitySource.StartActivity("reader.open", ActivityKind.Internal);
        readActivity?.SetTag("file.protocol", fileEvent.Protocol);
        readActivity?.SetTag("file.source_path", sourceRef.Path);
        var open = await reader.OpenReadAsync(sourceRef, ct).ConfigureAwait(false);
        return open;
    }

    /// <summary>
    /// Routes the file event and returns the first destination plan if any, or null when there are no plans.
    /// </summary>
    private async Task<Result<DestinationPlan?>> GetFirstDestinationPlanAsync(FileEvent fileEvent, CancellationToken ct)
    {
        var route = await _router.RouteAsync(fileEvent, ct).ConfigureAwait(false);
        if (route.IsFailure)
        {
            return Result<DestinationPlan?>.Failure(route.Error);
        }
        var plans = route.Value!;
        if (plans.Count == 0)
        {
            return Result<DestinationPlan?>.Success(null);
        }
        return Result<DestinationPlan?>.Success(plans[0]);
    }

    /// <summary>
    /// Applies idempotency guarding. Returns Result.Success(true) when processing should continue,
    /// Result.Success(false) when the event was already processed (and should be skipped), or a failure result
    /// if marking the event failed unexpectedly.
    /// </summary>
    private async Task<Result<bool>> EnsureFirstProcessingAsync(FileEvent fileEvent, CancellationToken ct)
    {
        var idemp = _idempotencyOptions.CurrentValue;
        if (!idemp.Enabled)
        {
            return Result<bool>.Success(true);
        }

        try
        {
            var key = $"file:{fileEvent.Id}";
            var ttl = TimeSpan.FromSeconds(Math.Max(1, idemp.TtlSeconds));
            var first = await _idempotencyStore.TryMarkProcessedAsync(key, ttl, ct).ConfigureAwait(false);
            if (!first)
            {
                _logger.LogInformation("Skipping already-processed file event {Id}", fileEvent.Id);
                return Result<bool>.Success(false);
            }
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            // Treat unexpected exception as failure so caller can decide policy (currently propagate failure)
            _logger.LogError(ex, "Idempotency mark failed for file event {Id}", fileEvent.Id);
            return Result<bool>.Failure(Error.Unspecified("Idempotency.MarkFailed", ex.Message));
        }
    }

    private IFileContentReader? SelectReader(string protocol)
    {
        if (string.Equals(protocol, "local", StringComparison.OrdinalIgnoreCase))
        {
            var local = _readers.FirstOrDefault(r => r is Infrastructure.Processing.LocalFileContentReader);
            if (local is not null) return local;
            // Fallback: allow tests with substituted reader to still work even if concrete type not present.
            return _readers.FirstOrDefault();
        }
        if (string.Equals(protocol, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            return _readers.FirstOrDefault(r => r is Infrastructure.Processing.SftpFileContentReader);
        }
        return null;
    }

    private string? ResolveLocalDestinationRoot(string destinationName)
    {
        var d = _destinations.CurrentValue.Local.FirstOrDefault(x => string.Equals(x.Name, destinationName, StringComparison.OrdinalIgnoreCase));
        return d?.RootPath;
    }

    private async Task DeleteSourceIfRequestedAsync(FileEvent fileEvent, CancellationToken ct)
    {
        if (!fileEvent.DeleteAfterTransfer) return; // event not requesting deletion
        var protocol = fileEvent.Protocol?.ToLowerInvariant();
        if (protocol == "local")
        {
            var path = fileEvent.Metadata.SourcePath;
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    _logger.LogDebug("Deleted local source file {Path}", path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed deleting local source file {Path}", path);
            }
            return;
        }

        if (protocol is not ("sftp" or "ftp")) return; // no deletion for other protocols yet

        var sourcePath = fileEvent.Metadata.SourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        if (!Uri.TryCreate(sourcePath, UriKind.Absolute, out var uri)) return;
        var host = uri.Host;
        var port = uri.Port;
        if (port <= 0 || uri.IsDefaultPort)
        {
            port = protocol == "sftp" ? 22 : 21;
        }
        var remotePath = uri.AbsolutePath;

        if (protocol == "sftp")
        {
            var src = _remoteSources.CurrentValue.Sftp.FirstOrDefault(s => s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port && remotePath.StartsWith(s.RemotePath, StringComparison.OrdinalIgnoreCase));
            if (src is null) return; // cannot resolve credentials
            try
            {
                var password = await _secretResolver.ResolveSecretAsync(src.PasswordSecretRef, ct).ConfigureAwait(false);
                await using var client = new Infrastructure.Remote.SftpRemoteFileClient(
                    logger: sftpClientLogger,
                    host: src.Host,
                    port: src.Port,
                    username: src.Username ?? string.Empty, password, null, null
                );
                await client.ConnectAsync(ct).ConfigureAwait(false);
                await client.DeleteAsync(remotePath, ct).ConfigureAwait(false);
                _logger.LogDebug("Deleted remote SFTP file {Path}", remotePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed deleting remote SFTP file {Path}", remotePath);
            }
        }
        else if (protocol == "ftp")
        {
            var src = _remoteSources.CurrentValue.Ftp.FirstOrDefault(s => s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port && remotePath.StartsWith(s.RemotePath, StringComparison.OrdinalIgnoreCase));
            if (src is null) return;
            try
            {
                var password = await _secretResolver.ResolveSecretAsync(src.PasswordSecretRef, ct).ConfigureAwait(false);
                var ftpClient = new Infrastructure.Remote.FtpRemoteFileClient(
                    logger: ftpClientLogger,
                    host: src.Host,
                    port: src.Port,
                    username: src.Username,
                    password: password,
                    passive: src.Passive
                );
                await ftpClient.ConnectAsync(ct).ConfigureAwait(false);
                await ftpClient.DeleteAsync(remotePath, ct).ConfigureAwait(false);
                await ftpClient.DisposeAsync();
                _logger.LogDebug("Deleted remote FTP file {Path}", remotePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed deleting remote FTP file {Path}", remotePath);
            }
        }
    }
}
