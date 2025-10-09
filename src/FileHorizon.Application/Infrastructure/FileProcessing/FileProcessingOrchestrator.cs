using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Common.Telemetry;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Remote;
using FileHorizon.Application.Models;
using FileHorizon.Application.Models.Notifications;
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
    Abstractions.IIdempotencyStore idempotencyStore,
    IFileProcessedNotifier notifier,
    IFileProcessingTelemetry telemetry,
    ISftpClientFactory sftpFactory,
    ISecretResolver secretResolver,
    ILogger<SftpRemoteFileClient> sftpClientLogger,
    ILogger<FtpRemoteFileClient> ftpClientLogger,
    ILogger<FileProcessingOrchestrator> logger) : IFileProcessor
{
    private readonly IFileRouter _router = router;
    private readonly IEnumerable<IFileContentReader> _readers = readers;
    private readonly IEnumerable<IFileSink> _sinks = sinks;
    private readonly IOptionsMonitor<DestinationsOptions> _destinations = destinations;
    private readonly IOptionsMonitor<IdempotencyOptions> _idempotencyOptions = idempotencyOptions;
    private readonly Abstractions.IIdempotencyStore _idempotencyStore = idempotencyStore;
    private readonly IFileProcessedNotifier _notifier = notifier;
    private readonly IFileProcessingTelemetry _telemetry = telemetry;
    private readonly IOptionsMonitor<RemoteFileSourcesOptions> _remoteSources = remoteSources;
    private readonly ISftpClientFactory _sftpFactory = sftpFactory;
    private readonly ISecretResolver _secretResolver = secretResolver;
    private readonly ILogger<SftpRemoteFileClient> sftpClientLogger = sftpClientLogger;
    private readonly ILogger<FtpRemoteFileClient> ftpClientLogger = ftpClientLogger;
    private readonly ILogger<FileProcessingOrchestrator> _logger = logger;

    public async Task<Result> ProcessAsync(FileEvent fileEvent, CancellationToken ct)
    {
        var overallSw = Stopwatch.StartNew();
        using var activity = TelemetryInstrumentation.ActivitySource.StartActivity("file.orchestrate", ActivityKind.Internal);
        activity?.SetTag("file.protocol", fileEvent.Protocol);
        activity?.SetTag("file.source_path", fileEvent.Metadata.SourcePath);

        // Idempotency check (optional)
        var idemp = _idempotencyOptions.CurrentValue;
        if (idemp.Enabled)
        {
            var key = $"file:{fileEvent.Id}";
            var ttl = TimeSpan.FromSeconds(Math.Max(1, idemp.TtlSeconds));
            var first = await _idempotencyStore.TryMarkProcessedAsync(key, ttl, ct).ConfigureAwait(false);
            if (!first)
            {
                _logger.LogInformation("Skipping already-processed file event {Id}", fileEvent.Id);
                return Result.Success();
            }
        }

        // Route to destinations
        var route = await _router.RouteAsync(fileEvent, ct).ConfigureAwait(false);
        if (route.IsFailure)
        {
            await PublishFailureAsync(fileEvent, ProcessingStatus.Failure, route.Error, overallSw.Elapsed, ct).ConfigureAwait(false);
            return Result.Failure(route.Error);
        }
        var plans = route.Value!;
        if (plans.Count == 0)
        {
            return Result.Success(); // nothing to do
        }

        // First cut: handle single destination only
        var plan = plans[0];

        // Resolve destination root from options for local destination type
        var destRoot = ResolveLocalDestinationRoot(plan.DestinationName);
        if (destRoot is null)
        {
            var err = Error.Validation.Invalid($"Unknown destination '{plan.DestinationName}'");
            _logger.LogWarning("Unknown destination {Dest}", plan.DestinationName);
            await PublishFailureAsync(fileEvent, ProcessingStatus.Failure, err, overallSw.Elapsed, ct).ConfigureAwait(false);
            return Result.Failure(err);
        }

        // Select reader based on protocol
        var reader = SelectReader(fileEvent.Protocol);
        if (reader is null)
        {
            var err = Error.Validation.Invalid($"No reader for protocol '{fileEvent.Protocol}'");
            await PublishFailureAsync(fileEvent, ProcessingStatus.Failure, err, overallSw.Elapsed, ct).ConfigureAwait(false);
            return Result.Failure(err);
        }

        // For now, we only support local sink
        var sink = _sinks.FirstOrDefault(s => string.Equals(s.Name, "Local", StringComparison.OrdinalIgnoreCase));
        if (sink is null)
        {
            var err = Error.Unspecified("Sink.LocalMissing", "Local sink not registered");
            await PublishFailureAsync(fileEvent, ProcessingStatus.Failure, err, overallSw.Elapsed, ct).ConfigureAwait(false);
            return Result.Failure(err);
        }

        var sourceRef = new FileReference(
            Scheme: fileEvent.Protocol,
            Host: null,
            Port: null,
            Path: fileEvent.Metadata.SourcePath,
            SourceName: null);

        var targetRef = new FileReference(
            Scheme: "local",
            Host: null,
            Port: null,
            Path: System.IO.Path.Combine(destRoot, plan.TargetPath),
            SourceName: plan.DestinationName);

        // Instrument reader open
        Result<Stream> open;
        using (var readActivity = TelemetryInstrumentation.ActivitySource.StartActivity("reader.open", ActivityKind.Internal))
        {
            readActivity?.SetTag("file.protocol", fileEvent.Protocol);
            readActivity?.SetTag("file.source_path", sourceRef.Path);
            open = await reader.OpenReadAsync(sourceRef, ct).ConfigureAwait(false);
        }
        if (open.IsFailure)
        {
            await PublishFailureAsync(fileEvent, ProcessingStatus.Failure, open.Error, overallSw.Elapsed, ct).ConfigureAwait(false);
            return Result.Failure(open.Error);
        }

        await using var stream = open.Value!;
        var write = await sink.WriteAsync(targetRef, stream, plan.Options, ct).ConfigureAwait(false);
        if (write.IsFailure)
        {
            await PublishFailureAsync(fileEvent, ProcessingStatus.Failure, write.Error, overallSw.Elapsed, ct).ConfigureAwait(false);
            return write; // propagate
        }
        // Source deletion (local or remote) if event requests it
        await DeleteSourceIfRequestedAsync(fileEvent, ct).ConfigureAwait(false);
        var duration = overallSw.Elapsed;
        await PublishSuccessAsync(fileEvent, plan, duration, ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task PublishSuccessAsync(FileEvent fileEvent, DestinationPlan plan, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var notification = FileProcessedNotification.Create(
                protocol: fileEvent.Protocol,
                fullPath: fileEvent.Metadata.SourcePath,
                sizeBytes: fileEvent.Metadata.SizeBytes,
                lastModifiedUtc: fileEvent.Metadata.LastModifiedUtc,
                status: ProcessingStatus.Success,
                processingDuration: duration,
                idempotencyKey: fileEvent.Id,
                correlationId: fileEvent.Id,
                completedUtc: DateTimeOffset.UtcNow,
                destinations: new List<DestinationResult>
                {
                    new(
                        DestinationType: "local",
                        DestinationIdentifier: plan.DestinationName,
                        Success: true,
                        BytesWritten: fileEvent.Metadata.SizeBytes,
                        Latency: null)
                }
            );
            var publish = await _notifier.PublishAsync(notification, ct).ConfigureAwait(false);
            if (publish.IsFailure)
            {
                _logger.LogDebug("FileProcessedNotifier publish failure suppressed (success path): {ErrorCode}", publish.Error.Code);
                _telemetry.RecordNotificationFailure(publish.Error.Code);
            }
            else
            {
                _telemetry.RecordNotificationSuccess(sw.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notifier publish threw (success path suppressed)");
            _telemetry.RecordNotificationFailure("exception");
        }
    }

    private async Task PublishFailureAsync(FileEvent fileEvent, ProcessingStatus status, Error error, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var notification = FileProcessedNotification.Create(
                protocol: fileEvent.Protocol,
                fullPath: fileEvent.Metadata.SourcePath,
                sizeBytes: fileEvent.Metadata.SizeBytes,
                lastModifiedUtc: fileEvent.Metadata.LastModifiedUtc,
                status: status,
                processingDuration: duration,
                idempotencyKey: fileEvent.Id,
                correlationId: fileEvent.Id,
                completedUtc: DateTimeOffset.UtcNow,
                destinations: new List<DestinationResult>() // no destinations succeeded
            );
            var publish = await _notifier.PublishAsync(notification, ct).ConfigureAwait(false);
            if (publish.IsFailure)
            {
                _logger.LogDebug("FileProcessedNotifier publish failure suppressed (failure path): {ErrorCode}", publish.Error.Code);
                _telemetry.RecordNotificationFailure(publish.Error.Code);
            }
            else
            {
                _telemetry.RecordNotificationSuccess(sw.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notifier publish threw (failure path suppressed)");
            _telemetry.RecordNotificationFailure("exception");
        }
    }

    private IFileContentReader? SelectReader(string protocol)
    {
        if (string.Equals(protocol, "local", StringComparison.OrdinalIgnoreCase))
        {
            return _readers.FirstOrDefault(r => r is Infrastructure.Processing.LocalFileContentReader);
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
