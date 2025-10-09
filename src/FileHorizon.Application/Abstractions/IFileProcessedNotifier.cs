namespace FileHorizon.Application.Abstractions;

using FileHorizon.Application.Models.Notifications;
using FileHorizon.Application.Common;

/// <summary>
/// Publishes a notification after a file has completed processing. Implementations may deliver
/// to external messaging systems. Expected to be lightweight and non-blocking; failures should
/// not crash the pipeline (return Failure instead for telemetry/logging).
/// </summary>
public interface IFileProcessedNotifier
{
    Task<Result> PublishAsync(FileProcessedNotification notification, CancellationToken ct);
}