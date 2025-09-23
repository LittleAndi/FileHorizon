namespace FileHorizon.Application.Abstractions;

public interface IFileReadinessChecker
{
    /// <summary>
    /// Determines whether a remote file is ready for processing (e.g. size stable for required duration).
    /// </summary>
    Task<bool> IsReadyAsync(IRemoteFileInfo file, FileObservationSnapshot? previousSnapshot, CancellationToken ct);
}

public sealed record FileObservationSnapshot(long Size, DateTimeOffset LastWriteTimeUtc, DateTimeOffset FirstObservedUtc, DateTimeOffset LastObservedUtc);
