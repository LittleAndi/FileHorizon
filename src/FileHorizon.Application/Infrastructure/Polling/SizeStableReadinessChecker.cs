using FileHorizon.Application.Abstractions;

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// A readiness checker that ensures the file size and last write timestamp are unchanged for a configured stability window.
/// The window is derived from source MinStableSeconds and enforced by caller passing the previous snapshot.
/// </summary>
public sealed class SizeStableReadinessChecker : IFileReadinessChecker
{
    private readonly TimeSpan _stabilityWindow;

    public SizeStableReadinessChecker(TimeSpan stabilityWindow)
    {
        _stabilityWindow = stabilityWindow < TimeSpan.Zero ? TimeSpan.Zero : stabilityWindow;
    }

    public Task<bool> IsReadyAsync(IRemoteFileInfo file, FileObservationSnapshot? previousSnapshot, CancellationToken ct)
    {
        if (previousSnapshot is null)
        {
            // Need at least one prior observation to confirm stability unless window is zero.
            return Task.FromResult(_stabilityWindow == TimeSpan.Zero);
        }

        // If size changed or last write changed since previous snapshot, not yet stable.
        if (previousSnapshot.Size != file.Size || previousSnapshot.LastWriteTimeUtc != file.LastWriteTimeUtc)
        {
            return Task.FromResult(false);
        }

        var stableDuration = DateTimeOffset.UtcNow - previousSnapshot.LastObservedUtc;
        var ready = stableDuration >= _stabilityWindow;
        return Task.FromResult(ready);
    }
}
