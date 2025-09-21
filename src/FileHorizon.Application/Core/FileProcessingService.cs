using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Models;
using Microsoft.Extensions.Logging;
using FileHorizon.Application.Common.Telemetry;
using System.Diagnostics;

namespace FileHorizon.Application.Core;

public interface IFileProcessingService
{
    Task<Result> HandleAsync(FileEvent fileEvent, CancellationToken ct);
}

public sealed class FileProcessingService : IFileProcessingService
{
    private readonly IFileProcessor _fileProcessor;
    private readonly ILogger<FileProcessingService> _logger;

    public FileProcessingService(IFileProcessor fileProcessor, ILogger<FileProcessingService> logger)
    {
        _fileProcessor = fileProcessor;
        _logger = logger;
    }

    public async Task<Result> HandleAsync(FileEvent fileEvent, CancellationToken ct)
    {
        _logger.LogDebug("Processing file event {FileId} protocol={Protocol} path={Path}", fileEvent.Id, fileEvent.Protocol, fileEvent.Metadata.SourcePath);

        var start = Stopwatch.GetTimestamp();
        using var activity = TelemetryInstrumentation.ActivitySource.StartActivity("file.process", ActivityKind.Internal);
        activity?.SetTag("file.id", fileEvent.Id);
        activity?.SetTag("file.protocol", fileEvent.Protocol);
        activity?.SetTag("file.source_path", fileEvent.Metadata.SourcePath);
        activity?.SetTag("file.size_bytes", fileEvent.Metadata.SizeBytes);

        var result = await _fileProcessor.ProcessAsync(fileEvent, ct).ConfigureAwait(false);
        var elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
        TelemetryInstrumentation.ProcessingDurationMs.Record(elapsedMs, KeyValuePair.Create<string, object?>("file.protocol", fileEvent.Protocol));

        if (!result.IsSuccess)
        {
            TelemetryInstrumentation.FilesFailed.Add(1, KeyValuePair.Create<string, object?>("file.protocol", fileEvent.Protocol));
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.ToString());
            _logger.LogWarning("File event {FileId} failed: {Error}", fileEvent.Id, result.Error);
        }
        else
        {
            TelemetryInstrumentation.FilesProcessed.Add(1, KeyValuePair.Create<string, object?>("file.protocol", fileEvent.Protocol));
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogDebug("File event {FileId} processed successfully", fileEvent.Id);
        }
        return result;
    }
}